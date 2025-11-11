// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Linq;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.McpGateway.Management.Authorization;
using Microsoft.McpGateway.Management.Contracts;
using Microsoft.McpGateway.Management.Deployment;
using Microsoft.McpGateway.Management.Extensions;
using Microsoft.McpGateway.Management.Store;

namespace Microsoft.McpGateway.Management.Service
{
    /// <summary>
    /// Service for managing tool deployments.
    /// Tools are deployed the same way as adapters, but with additional tool definition metadata.
    /// </summary>
    public class ToolManagementService : IToolManagementService
    {
        private const string NamePattern = "^[a-z0-9-]+$";
        private readonly IAdapterDeploymentManager _deploymentManager;
        private readonly IToolResourceStore _store;
        private readonly IPermissionProvider _permissionProvider;
        private readonly ILogger _logger;

        public ToolManagementService(
            IAdapterDeploymentManager adapterDeploymentManager,
            IToolResourceStore store,
            IPermissionProvider permissionProvider,
            ILogger<ToolManagementService> logger)
        {
            _deploymentManager = adapterDeploymentManager ?? throw new ArgumentNullException(nameof(adapterDeploymentManager));
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _permissionProvider = permissionProvider ?? throw new ArgumentNullException(nameof(permissionProvider));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<ToolResource> CreateAsync(ClaimsPrincipal accessContext, ToolData request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(accessContext);
            ArgumentNullException.ThrowIfNull(request);

            if (!Regex.IsMatch(request.Name, NamePattern))
                throw new ArgumentException("Name must contain only lowercase letters, numbers, and dashes.");

            var existing = await _store.TryGetAsync(request.Name, cancellationToken).ConfigureAwait(false);
            if (existing != null)
            {
                _logger.LogWarning("/tools/{name} already exists.", request.Name.Sanitize());
                throw new ArgumentException("The tool with the same name already exists.");
            }

            var tool = ToolResource.Create(request, accessContext.GetUserId()!, DateTimeOffset.UtcNow);

            _logger.LogInformation("Start creating /tools/{name}.", request.Name.Sanitize());

            // Deploy using the same adapter deployment manager
            _logger.LogInformation("Start kubernetes deployment for /tools/{name}.", request.Name.Sanitize());
            // Convert ToolData to AdapterData for deployment
            var adapterData = (AdapterData)request;
            await _deploymentManager.CreateDeploymentAsync(adapterData, ResourceType.Tool, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Start update internal storage for /tools/{name}.", request.Name.Sanitize());
            await _store.UpsertAsync(tool, cancellationToken).ConfigureAwait(false);
            return tool;
        }

        public async Task<ToolResource?> GetAsync(ClaimsPrincipal accessContext, string name, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(accessContext);
            ArgumentException.ThrowIfNullOrEmpty(name);

            _logger.LogInformation("Start getting /tools/{name}.", name.Sanitize());
            var tool = await _store.TryGetAsync(name, cancellationToken).ConfigureAwait(false);
            if (tool == null)
            {
                return null;
            }

            await EnsureAccessAsync(accessContext, tool, Operation.Read).ConfigureAwait(false);
            return tool;
        }

        public async Task<ToolResource> UpdateAsync(ClaimsPrincipal accessContext, ToolData request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(accessContext);
            ArgumentNullException.ThrowIfNull(request);

            _logger.LogInformation("Start updating /tools/{name}.", request.Name.Sanitize());

            var existing = await _store.TryGetAsync(request.Name, cancellationToken).ConfigureAwait(false)
                ?? throw new ArgumentException("The tool does not exist and cannot be updated.");

            await EnsureAccessAsync(accessContext, existing, Operation.Write).ConfigureAwait(false);

            // Throw if any change on unchangeable fields
            if (existing.Name != request.Name)
            {
                throw new ArgumentException("The tool does not allow change on the submitted field.");
            }

            var updated = ToolResource.Create(request, existing.CreatedBy, existing.CreatedAt);

            // Only trigger deployment if any change on the deployment configuration.
            if (existing.ReplicaCount != request.ReplicaCount ||
                existing.ImageName != request.ImageName ||
                existing.ImageVersion != request.ImageVersion ||
                !existing.EnvironmentVariables.OrderBy(kv => kv.Key).SequenceEqual(request.EnvironmentVariables.OrderBy(kv => kv.Key)))
            {
                // Convert ToolData to AdapterData for deployment
                var adapterData = (AdapterData)request;
                await _deploymentManager.UpdateDeploymentAsync(adapterData, ResourceType.Tool, cancellationToken).ConfigureAwait(false);
            }

            await _store.UpsertAsync(updated, cancellationToken).ConfigureAwait(false);
            return updated;
        }

        public async Task DeleteAsync(ClaimsPrincipal accessContext, string name, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(accessContext);
            ArgumentException.ThrowIfNullOrEmpty(name);

            _logger.LogInformation("Start deleting /tools/{name}.", name.Sanitize());
            var existing = await _store.TryGetAsync(name, cancellationToken).ConfigureAwait(false)
                ?? throw new ArgumentException("The tool does not exist.");

            await EnsureAccessAsync(accessContext, existing, Operation.Write).ConfigureAwait(false);

            _logger.LogInformation("Start deleting storage record for /tools/{name}.", name.Sanitize());
            await _store.DeleteAsync(name, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Start deleting Kubernetes deployment for /tools/{name}.", name.Sanitize());
            await _deploymentManager.DeleteDeploymentAsync(name, cancellationToken).ConfigureAwait(false);
        }

        public async Task<IEnumerable<ToolResource>> ListAsync(ClaimsPrincipal accessContext, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(accessContext);

            _logger.LogInformation("Start listing /tools for user.");
            var toolResources = (await _store.ListAsync(cancellationToken).ConfigureAwait(false)).ToList();
            var allowedResources = await _permissionProvider.CheckAccessAsync(accessContext, toolResources, Operation.Read).ConfigureAwait(false);

            var filteredCount = toolResources.Count - allowedResources.Length;
            if (filteredCount > 0)
            {
                _logger.LogInformation("Filtered {count} tool resources due to authorization.", filteredCount);
            }

            return allowedResources;
        }

        private async Task EnsureAccessAsync(ClaimsPrincipal accessContext, ToolResource resource, Operation operation)
        {
            ArgumentNullException.ThrowIfNull(accessContext);
            ArgumentNullException.ThrowIfNull(resource);

            if (await _permissionProvider.CheckAccessAsync(accessContext, resource, operation).ConfigureAwait(false))
            {
                if (operation == Operation.Write)
                {
                    _logger.LogInformation("User {userId} is authorized for write operations on tool {resourceId}.", accessContext.GetUserId(), resource.Name.Sanitize());
                }

                return;
            }

            var operationName = operation.ToString().ToLowerInvariant();
            _logger.LogWarning("User {userId} is denied {operation} access for tool {resourceId}.", accessContext.GetUserId(), operationName, resource.Name.Sanitize());
            throw new UnauthorizedAccessException("You do not have permission to perform the operation.");
        }
    }
}
