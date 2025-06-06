// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using k8s;

namespace Microsoft.McpGateway.Management.Deployment
{
    /// <summary>
    /// Factory interface for providing Kubernetes clients scoped to a specific environment or configuration.
    /// </summary>
    public interface IKubernetesClientFactory
    {
        /// <summary>
        /// Asynchronously retrieves a Kubernetes client instance connected to the appropriate AKS cluster.
        /// </summary>
        /// <param name="cancellation">Token to cancel the operation.</param>
        /// <returns>A task that resolves to an <see cref="IKubernetes"/> client instance.</returns>
        Task<IKubernetes> GetKubernetesClientAsync(CancellationToken cancellation);
    }
}
