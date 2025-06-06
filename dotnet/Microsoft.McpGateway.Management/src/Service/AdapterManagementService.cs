// -// Copyright (c) Microsoft Corporation.
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
    public class AdapterManagementService(IAdapterDeploymentManager adapterDeploymentManager, IAdapterResourceStore store, ILogger<AdapterManagementService> logger) : IAdapterManagementService
    {
        private const string NamePattern = "^[a-z0-9-]+$";
        private readonly IAdapterDeploymentManager _deploymentManager = adapterDeploymentManager ?? throw new ArgumentNullException(nameof(adapterDeploymentManager));
        private readonly IAdapterResourceStore _store = store ?? throw new ArgumentNullException(nameof(store));
        private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        public async Task<AdapterResource> CreateAsync(ClaimsPrincipal accessContext, AdapterData request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(accessContext);
            ArgumentNullException.ThrowIfNull(request);

            if (!Regex.IsMatch(request.Name, NamePattern))
                throw new ArgumentException("Name must contain only lowercase letters, numbers, and dashes.");

            var existing = await _store.TryGetAsync(request.Name, cancellationToken).ConfigureAwait(false);
            if (existing != null)
            {
                logger.LogWarning("/adapters/{name} already exists.", request.Name.Sanitize());
                throw new ArgumentException("The adapter with the same name already exist.");
            }

            var adapter = AdapterResource.Create(request, accessContext.GetUserId()!, DateTimeOffset.UtcNow);

            logger.LogInformation("Start creating /adapters/{name}.", request.Name.Sanitize());

            logger.LogInformation("Start kubernetes deployment for /adapters/{name}.", request.Name.Sanitize());
            await _deploymentManager.CreateDeploymentAsync(request, cancellationToken).ConfigureAwait(false);

            logger.LogInformation("Start update internal storage for /adapters/{name}.", request.Name.Sanitize());
            await _store.UpsertAsync(adapter, cancellationToken).ConfigureAwait(false);
            return adapter;
        }

        public async Task<AdapterResource?> GetAsync(ClaimsPrincipal accessContext, string name, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(accessContext, nameof(accessContext));
            ArgumentException.ThrowIfNullOrEmpty(name, nameof(name));

            logger.LogInformation("Start getting /adapters/{name}.", name.Sanitize());

            await CheckReadAccessAsync(accessContext, name, cancellationToken).ConfigureAwait(false);
            return await _store.TryGetAsync(name, cancellationToken).ConfigureAwait(false);
        }

        public async Task<AdapterResource> UpdateAsync(ClaimsPrincipal accessContext, AdapterData request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(accessContext);
            ArgumentNullException.ThrowIfNull(request);

            logger.LogInformation("Start updating /adapters/{name}.", request.Name.Sanitize());

            await CheckWriteAccessAsync(accessContext, request.Name, cancellationToken).ConfigureAwait(false);
            var existing = await _store.TryGetAsync(request.Name, cancellationToken).ConfigureAwait(false)
                ?? throw new ArgumentException("The adapter does not exist and cannot be updated.");

            // Throw if any change on Unchangeable fields
            if (existing.Name != request.Name)
            {
                throw new ArgumentException("The adapter does not allow change on the submitted field.");
            }

            var updated = AdapterResource.Create(request, existing.CreatedBy, existing.CreatedAt);

            // Only trigger deployment if any change on the deployment configuration.
            if (existing.ReplicaCount != request.ReplicaCount ||
                existing.ImageName != request.ImageName ||
                existing.ImageVersion != request.ImageVersion ||
                !existing.EnvironmentVariables.OrderBy(kv => kv.Key).SequenceEqual(request.EnvironmentVariables.OrderBy(kv => kv.Key)))
            {
                await _deploymentManager.UpdateDeploymentAsync(updated, cancellationToken).ConfigureAwait(false);
            }

            await _store.UpsertAsync(updated, cancellationToken).ConfigureAwait(false);
            return updated;
        }

        public async Task DeleteAsync(ClaimsPrincipal accessContext, string name, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(accessContext, nameof(accessContext));
            ArgumentException.ThrowIfNullOrEmpty(name, nameof(name));

            logger.LogInformation("Start deleting /adapters/{name}.", name.Sanitize());
            await CheckWriteAccessAsync(accessContext, name, cancellationToken).ConfigureAwait(false);

            logger.LogInformation("Start deleting storage record for /adapters/{name}.", name.Sanitize());
            await _store.DeleteAsync(name, cancellationToken).ConfigureAwait(false);

            logger.LogInformation("Start deleting Kubernetes deployment for /adapters/{name}.", name.Sanitize());
            await _deploymentManager.DeleteDeploymentAsync(name, cancellationToken).ConfigureAwait(false);
        }

        public async Task<IEnumerable<AdapterResource>> ListAsync(ClaimsPrincipal accessContext, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(accessContext, nameof(accessContext));

            logger.LogInformation("Start listing /adapters for user.");
            var adapterResources = await _store.ListAsync(cancellationToken).ConfigureAwait(false);
            var allowedResources = await CheckReadAccessAsync(accessContext, adapterResources, cancellationToken).ConfigureAwait(false);

            return allowedResources;
        }

        private async Task CheckWriteAccessAsync(ClaimsPrincipal accessContext, string resouceName, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(accessContext, nameof(accessContext));
            ArgumentException.ThrowIfNullOrEmpty(resouceName, nameof(resouceName));

            var existing = await _store.TryGetAsync(resouceName, cancellationToken).ConfigureAwait(false)
                    ?? throw new ArgumentException("The adapter does not exist.");
            var allowedAccess = existing.CreatedBy == accessContext.GetUserId();

            if (!allowedAccess)
            {
                _logger.LogWarning("User {userId} is denied access for resource {resourceId} after checking access.", accessContext.GetUserId(), resouceName.Sanitize());
                throw new UnauthorizedAccessException("You do not have permission to perform the operation.");
            }

            _logger.LogInformation("User {userId} is authorized for resource {resourceId} checking access", accessContext.GetUserId(), resouceName.Sanitize());
        }

        // Allow all read access
        private Task CheckReadAccessAsync(ClaimsPrincipal accessContext, string resourceName, CancellationToken cancellationToken) => Task.CompletedTask;

        // Allow all read access
        private Task<IEnumerable<AdapterResource>> CheckReadAccessAsync(ClaimsPrincipal accessContext, IEnumerable<AdapterResource> resources, CancellationToken cancellationToken) => Task.FromResult(resources);
    }
}
