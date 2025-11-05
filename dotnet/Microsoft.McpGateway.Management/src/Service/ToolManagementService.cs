// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
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
        private readonly ILogger _logger;

        public ToolManagementService(
            IAdapterDeploymentManager adapterDeploymentManager,
            IToolResourceStore store,
            ILogger<ToolManagementService> logger)
        {
            _deploymentManager = adapterDeploymentManager ?? throw new ArgumentNullException(nameof(adapterDeploymentManager));
            _store = store ?? throw new ArgumentNullException(nameof(store));
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
            await CheckReadAccessAsync(accessContext, name, cancellationToken).ConfigureAwait(false);
            return await _store.TryGetAsync(name, cancellationToken).ConfigureAwait(false);
        }

        public async Task<ToolResource> UpdateAsync(ClaimsPrincipal accessContext, ToolData request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(accessContext);
            ArgumentNullException.ThrowIfNull(request);

            _logger.LogInformation("Start updating /tools/{name}.", request.Name.Sanitize());

            await CheckWriteAccessAsync(accessContext, request.Name, cancellationToken).ConfigureAwait(false);
            var existing = await _store.TryGetAsync(request.Name, cancellationToken).ConfigureAwait(false)
                ?? throw new ArgumentException("The tool does not exist and cannot be updated.");

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
            await CheckWriteAccessAsync(accessContext, name, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Start deleting storage record for /tools/{name}.", name.Sanitize());
            await _store.DeleteAsync(name, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Start deleting Kubernetes deployment for /tools/{name}.", name.Sanitize());
            await _deploymentManager.DeleteDeploymentAsync(name, cancellationToken).ConfigureAwait(false);
        }

        public async Task<IEnumerable<ToolResource>> ListAsync(ClaimsPrincipal accessContext, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(accessContext);

            _logger.LogInformation("Start listing /tools for user.");
            var toolResources = await _store.ListAsync(cancellationToken).ConfigureAwait(false);
            var allowedResources = await CheckReadAccessAsync(accessContext, toolResources, cancellationToken).ConfigureAwait(false);

            return allowedResources;
        }

        private async Task CheckWriteAccessAsync(ClaimsPrincipal accessContext, string resourceName, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(accessContext);
            ArgumentException.ThrowIfNullOrEmpty(resourceName);

            var existing = await _store.TryGetAsync(resourceName, cancellationToken).ConfigureAwait(false)
                    ?? throw new ArgumentException("The tool does not exist.");
            var allowedAccess = existing.CreatedBy == accessContext.GetUserId();

            if (!allowedAccess)
            {
                _logger.LogWarning("User {userId} is denied access for resource {resourceId} after checking access.", accessContext.GetUserId(), resourceName.Sanitize());
                throw new UnauthorizedAccessException("You do not have permission to perform the operation.");
            }

            _logger.LogInformation("User {userId} is authorized for resource {resourceId} checking access", accessContext.GetUserId(), resourceName.Sanitize());
        }

        // Allow all read access
        private Task CheckReadAccessAsync(ClaimsPrincipal accessContext, string resourceName, CancellationToken cancellationToken) => Task.CompletedTask;

        // Allow all read access
        private Task<IEnumerable<ToolResource>> CheckReadAccessAsync(ClaimsPrincipal accessContext, IEnumerable<ToolResource> resources, CancellationToken cancellationToken) => Task.FromResult(resources);
    }
}
