// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Linq;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.McpGateway.Management.Authorization;
using Microsoft.McpGateway.Management.Contracts;
using Microsoft.McpGateway.Management.Extensions;
using Microsoft.McpGateway.Management.Foundry;
using Microsoft.McpGateway.Management.Store;

namespace Microsoft.McpGateway.Management.Service
{
    /// <summary>
    /// Service for managing agent definitions. Pure metadata CRUD; no
    /// Kubernetes deployment side-effects.
    /// </summary>
    public class AgentManagementService : IAgentManagementService
    {
        private const string NamePattern = "^[a-z0-9-]+$";

        private readonly IAgentResourceStore _store;
        private readonly IToolResourceStore _toolStore;
        private readonly IPermissionProvider _permissionProvider;
        private readonly ILogger _logger;

        public AgentManagementService(
            IAgentResourceStore store,
            IToolResourceStore toolStore,
            IPermissionProvider permissionProvider,
            IBuiltinToolAuthorizer builtinToolAuthorizer,
            ILogger<AgentManagementService> logger)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _toolStore = toolStore ?? throw new ArgumentNullException(nameof(toolStore));
            _permissionProvider = permissionProvider ?? throw new ArgumentNullException(nameof(permissionProvider));
            ArgumentNullException.ThrowIfNull(builtinToolAuthorizer);
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<AgentResource> CreateAsync(ClaimsPrincipal accessContext, AgentData request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(accessContext);
            ArgumentNullException.ThrowIfNull(request);

            if (!Regex.IsMatch(request.Name, NamePattern))
                throw new ArgumentException("Name must contain only lowercase letters, numbers, and dashes.");

            var existing = await _store.TryGetAsync(request.Name, cancellationToken).ConfigureAwait(false);
            if (existing != null)
            {
                _logger.LogWarning("/agents/{name} already exists.", request.Name.Sanitize());
                throw new ArgumentException("An agent with the same name already exists.");
            }

            await ValidateToolReferencesAsync(accessContext, request, cancellationToken).ConfigureAwait(false);

            var agent = AgentResource.Create(request, accessContext.GetUserId()!, DateTimeOffset.UtcNow);

            _logger.LogInformation("Creating /agents/{name}.", request.Name.Sanitize());
            await _store.UpsertAsync(agent, cancellationToken).ConfigureAwait(false);
            return agent;
        }

        public async Task<AgentResource?> GetAsync(ClaimsPrincipal accessContext, string name, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(accessContext);
            ArgumentException.ThrowIfNullOrEmpty(name);

            _logger.LogInformation("Getting /agents/{name}.", name.Sanitize());
            var agent = await _store.TryGetAsync(name, cancellationToken).ConfigureAwait(false);
            if (agent == null)
            {
                return null;
            }

            await EnsureAccessAsync(accessContext, agent, Operation.Read).ConfigureAwait(false);
            return agent;
        }

        public async Task<AgentResource> UpdateAsync(ClaimsPrincipal accessContext, AgentData request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(accessContext);
            ArgumentNullException.ThrowIfNull(request);

            _logger.LogInformation("Updating /agents/{name}.", request.Name.Sanitize());

            var existing = await _store.TryGetAsync(request.Name, cancellationToken).ConfigureAwait(false)
                ?? throw new ArgumentException("The agent does not exist and cannot be updated.");

            await EnsureAccessAsync(accessContext, existing, Operation.Write).ConfigureAwait(false);

            if (existing.Name != request.Name)
            {
                throw new ArgumentException("The agent does not allow change on the submitted field.");
            }

            await ValidateToolReferencesAsync(accessContext, request, cancellationToken).ConfigureAwait(false);

            var updated = AgentResource.Create(request, existing.CreatedBy, existing.CreatedAt);
            await _store.UpsertAsync(updated, cancellationToken).ConfigureAwait(false);
            return updated;
        }

        public async Task DeleteAsync(ClaimsPrincipal accessContext, string name, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(accessContext);
            ArgumentException.ThrowIfNullOrEmpty(name);

            _logger.LogInformation("Deleting /agents/{name}.", name.Sanitize());
            var existing = await _store.TryGetAsync(name, cancellationToken).ConfigureAwait(false)
                ?? throw new ArgumentException("The agent does not exist.");

            await EnsureAccessAsync(accessContext, existing, Operation.Write).ConfigureAwait(false);

            await _store.DeleteAsync(name, cancellationToken).ConfigureAwait(false);
        }

        public async Task<IEnumerable<AgentResource>> ListAsync(ClaimsPrincipal accessContext, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(accessContext);

            _logger.LogInformation("Listing /agents for user.");
            var resources = (await _store.ListAsync(cancellationToken).ConfigureAwait(false)).ToList();
            var allowed = await _permissionProvider.CheckAccessAsync(accessContext, resources, Operation.Read).ConfigureAwait(false);

            var filteredCount = resources.Count - allowed.Length;
            if (filteredCount > 0)
            {
                _logger.LogInformation("Filtered {count} agent resources due to authorization.", filteredCount);
            }

            return allowed;
        }

        private async Task EnsureAccessAsync(ClaimsPrincipal accessContext, AgentResource resource, Operation operation)
        {
            ArgumentNullException.ThrowIfNull(accessContext);
            ArgumentNullException.ThrowIfNull(resource);

            if (await _permissionProvider.CheckAccessAsync(accessContext, resource, operation).ConfigureAwait(false))
            {
                if (operation == Operation.Write)
                {
                    _logger.LogInformation("User {userId} is authorized for write operations on agent {resourceId}.", accessContext.GetUserId(), resource.Name.Sanitize());
                }

                return;
            }

            var operationName = operation.ToString().ToLowerInvariant();
            _logger.LogWarning("User {userId} is denied {operation} access for agent {resourceId}.", accessContext.GetUserId(), operationName, resource.Name.Sanitize());
            throw new UnauthorizedAccessException("You do not have permission to perform the operation.");
        }

        /// <summary>
        /// Validate every entry in <see cref="AgentData.Tools"/> at agent
        /// create/update time so an agent can't be persisted with references
        /// the caller cannot themselves invoke. Each entry must be one of:
        /// <list type="bullet">
        ///   <item><description><c>mcp:&lt;tool-name&gt;</c> — a tool registered via <c>/tools</c> that the caller has read access to.</description></item>
        ///   <item><description><c>agent:&lt;agent-name&gt;</c> — a peer agent the caller has read access to.</description></item>
        /// </list>
        /// Throws <see cref="ArgumentException"/> for disabled, unknown / unprefixed
        /// names and missing references; throws
        /// <see cref="UnauthorizedAccessException"/> when the caller lacks
        /// read access on a referenced resource.
        /// </summary>
        private async Task ValidateToolReferencesAsync(ClaimsPrincipal accessContext, AgentData request, CancellationToken cancellationToken)
        {
            // Iterate defensively over a possibly null/empty list rather than using an
            // early return. With no entries this simply does no work; it also keeps the
            // per-entry authorization checks below from being guarded by a user-controlled
            // condition (avoids CodeQL cs/user-controlled-bypass-of-sensitive-method).
            foreach (var entry in request.Tools ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(entry))
                {
                    throw new ArgumentException("Tool entries must be non-empty strings.");
                }

                var colon = entry.IndexOf(':');
                if (colon <= 0 || colon == entry.Length - 1)
                {
                    throw new ArgumentException($"Tool entry '{entry}' is missing a recognized prefix (expected 'mcp:' or 'agent:').");
                }

                var prefix = entry.Substring(0, colon);
                var name = entry.Substring(colon + 1);

                switch (prefix)
                {
                    case "mcp":
                        var tool = await _toolStore.TryGetAsync(name, cancellationToken).ConfigureAwait(false)
                            ?? throw new ArgumentException($"MCP tool '{name}' referenced by agent does not exist.");
                        if (!await _permissionProvider.CheckAccessAsync(accessContext, tool, Operation.Read).ConfigureAwait(false))
                        {
                            _logger.LogWarning("User {userId} denied read access on tool {tool} while saving agent {agent}.", accessContext.GetUserId(), name.Sanitize(), request.Name.Sanitize());
                            throw new UnauthorizedAccessException($"You do not have permission to reference tool '{name}'.");
                        }
                        break;

                    case "agent":
                        if (string.Equals(name, request.Name, StringComparison.Ordinal))
                        {
                            throw new ArgumentException($"Agent '{request.Name}' cannot reference itself as a subagent.");
                        }
                        var peer = await _store.TryGetAsync(name, cancellationToken).ConfigureAwait(false)
                            ?? throw new ArgumentException($"Peer agent '{name}' referenced by agent does not exist.");
                        if (!await _permissionProvider.CheckAccessAsync(accessContext, peer, Operation.Read).ConfigureAwait(false))
                        {
                            _logger.LogWarning("User {userId} denied read access on peer agent {peer} while saving agent {agent}.", accessContext.GetUserId(), name.Sanitize(), request.Name.Sanitize());
                            throw new UnauthorizedAccessException($"You do not have permission to reference agent '{name}'.");
                        }
                        break;

                    case "builtin":
                        throw new ArgumentException($"Tool '{entry}' cannot be referenced. {BuiltinToolExecutor.DisabledMessage}");

                    default:
                        throw new ArgumentException($"Tool entry '{entry}' uses an unrecognized prefix '{prefix}:' (expected 'mcp:' or 'agent:').");
                }
            }
        }
    }
}
