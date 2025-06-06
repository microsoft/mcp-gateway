// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.McpGateway.Management.Contracts;

namespace Microsoft.McpGateway.Management.Deployment
{
    /// <summary>
    /// Contract for managing adapter deployments.
    /// </summary>
    public interface IAdapterDeploymentManager
    {
        /// <summary>
        /// Creates a new deployment asynchronously.
        /// </summary>
        /// <param name="request">The data for the adapter deployment.</param>
        /// <param name="cancellationToken">Token to cancel the operation.</param>
        Task CreateDeploymentAsync(AdapterData request, CancellationToken cancellationToken);

        /// <summary>
        /// Deletes an existing deployment asynchronously.
        /// </summary>
        /// <param name="name">The name of the adapter deployment to delete.</param>
        /// <param name="cancellationToken">Token to cancel the operation.</param>
        Task DeleteDeploymentAsync(string name, CancellationToken cancellationToken);

        /// <summary>
        /// Get an existing deployment status.
        /// </summary>
        /// <param name="name">The name of the adapter deployment</param>
        /// <param name="cancellationToken">Token to cancel the operation.</param>
        /// <returns>The deployment status.</returns>
        Task<AdapterStatus> GetDeploymentStatusAsync(string name, CancellationToken cancellationToken);

        /// <summary>
        /// Get the deployed adapter deployment logs.
        /// </summary>
        /// <param name="name">The name of the adapter deployment</param>
        /// <param name="cancellationToken">Token to cancel the operation.</param>
        /// <returns>The logs from the deployment pod.</returns>
        Task<string> GetDeploymentLogsAsync(string name, int ordinal, CancellationToken cancellationToken);

        /// <summary>
        /// Updates an existing deployment asynchronously.
        /// </summary>
        /// <param name="request">The data for the adapter deployment update.</param>
        /// <param name="cancellationToken">Token to cancel the operation.</param>
        Task UpdateDeploymentAsync(AdapterData request, CancellationToken cancellationToken);
    }
}
