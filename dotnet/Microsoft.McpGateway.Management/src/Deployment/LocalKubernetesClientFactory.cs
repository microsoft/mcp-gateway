// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using k8s;

namespace Microsoft.McpGateway.Management.Deployment
{
    public sealed class LocalKubernetesClientFactory : IKubernetesClientFactory
    {
        private readonly Kubernetes _client;

        public LocalKubernetesClientFactory() => _client = new Kubernetes(KubernetesClientConfiguration.BuildDefaultConfig());

        public Task<IKubernetes> GetKubernetesClientAsync(CancellationToken cancellation) => Task.FromResult<IKubernetes>(_client);
    }
}
