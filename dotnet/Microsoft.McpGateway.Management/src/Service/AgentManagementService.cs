// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Linq;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.McpGateway.Management.Authorization;
using Microsoft.McpGateway.Management.Contracts;
using Microsoft.McpGateway.Management.Extensions;
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
        private readonly IPermissionProvider _permissionProvider;
        private readonly ILogger _logger;

        public AgentManagementService(
            IAgentResourceStore store,
            IPermissionProvider permissionProvider,
            ILogger<AgentManagementService> logger)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _permissionProvider = permissionProvider ?? throw new ArgumentNullException(nameof(permissionProvider));
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
    }
}
