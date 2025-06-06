// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using k8s;
using k8s.Models;

namespace Microsoft.McpGateway.Management.Deployment
{
    public class KubeClient(IKubernetesClientFactory kubernetesClientFactory, string defaultNamespace = "default") : IKubeClientWrapper
    {
        private readonly string _defaultNamespace = defaultNamespace ?? throw new ArgumentNullException(nameof(defaultNamespace));
        private readonly Lazy<Task<IKubernetes>> _lazyClient = new(() => kubernetesClientFactory.GetKubernetesClientAsync(default));

        public async Task<V1StatefulSetList> ListStatefulSetsAsync(string? ns = null, CancellationToken cancellationToken = default)
        {
            var kubeClient = await _lazyClient.Value.ConfigureAwait(false);
            return await kubeClient.AppsV1.ListNamespacedStatefulSetAsync(ns ?? _defaultNamespace, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        public async Task<V1StatefulSet> UpsertStatefulSetAsync(V1StatefulSet statefulSet, string? ns = null, CancellationToken cancellationToken = default)
        {
            var kubeClient = await _lazyClient.Value.ConfigureAwait(false);
            return await kubeClient.AppsV1.CreateNamespacedStatefulSetAsync(statefulSet, ns ?? _defaultNamespace, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        public async Task<V1StatefulSet> ReplaceStatefulSetAsync(V1StatefulSet statefulSet, string statefulSetName, string? ns = null, CancellationToken ct = default)
        {
            var kubeClient = await _lazyClient.Value.ConfigureAwait(false);
            return await kubeClient.AppsV1.ReplaceNamespacedStatefulSetAsync(statefulSet, statefulSetName, ns ?? _defaultNamespace, cancellationToken: ct).ConfigureAwait(false);
        }

        public async Task<V1StatefulSet> PatchStatefulSetAsync(V1Patch patch, string statefulSetName, string? ns = null, CancellationToken ct = default)
        {
            var kubeClient = await _lazyClient.Value.ConfigureAwait(false);
            return await kubeClient.AppsV1.PatchNamespacedStatefulSetAsync(patch, statefulSetName, ns ?? _defaultNamespace, cancellationToken: ct).ConfigureAwait(false);
        }

        public async Task<V1Service> UpsertServiceAsync(V1Service service, string? ns = null, CancellationToken ct = default)
        {
            var kubeClient = await _lazyClient.Value.ConfigureAwait(false);
            return await kubeClient.CoreV1.CreateNamespacedServiceAsync(service, ns ?? _defaultNamespace, cancellationToken: ct).ConfigureAwait(false);
        }

        public async Task<V1StatefulSet> ReadStatefulSetAsync(string name, string? ns = null, CancellationToken cancellationToken = default)
        {
            var kubeClient = await _lazyClient.Value.ConfigureAwait(false);
            return await kubeClient.AppsV1.ReadNamespacedStatefulSetAsync(name, ns ?? _defaultNamespace, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        public async Task<V1Deployment> ReadDeploymentAsync(string name, string? ns = null, CancellationToken cancellationToken = default)
        {
            var kubeClient = await _lazyClient.Value.ConfigureAwait(false);
            return await kubeClient.AppsV1.ReadNamespacedDeploymentAsync(name, ns ?? _defaultNamespace, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        public async Task<V1Pod> ReadPodAsync(string name, string? ns = null, CancellationToken cancellationToken = default)
        {
            var kubeClient = await _lazyClient.Value.ConfigureAwait(false);
            return await kubeClient.CoreV1.ReadNamespacedPodAsync(name, ns ?? _defaultNamespace, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        public async Task<V1Status> DeleteStatefulSetAsync(string name, string? ns = null, CancellationToken cancellationToken = default)
        {
            var kubeClient = await _lazyClient.Value.ConfigureAwait(false);
            return await kubeClient.AppsV1.DeleteNamespacedStatefulSetAsync(name, ns ?? _defaultNamespace, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        public async Task<V1Service> DeleteServiceAsync(string name, string? ns = null, CancellationToken cancellationToken = default)
        {
            var kubeClient = await _lazyClient.Value.ConfigureAwait(false);
            return await kubeClient.CoreV1.DeleteNamespacedServiceAsync(name, ns ?? _defaultNamespace, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        public async Task<Stream> GetContainerLogStream(string name, int tailLines = 1000, string? ns = null, CancellationToken cancellationToken = default)
        {
            var kubeClient = await _lazyClient.Value.ConfigureAwait(false);
            var pod = await ReadPodAsync(name, ns ?? _defaultNamespace, cancellationToken).ConfigureAwait(false);

            if (pod.Status.Phase != "Running" && pod.Status.Phase != "Pending")
                throw new InvalidOperationException($"Pod '{name}' is not in health state: {pod.Status.Phase}");

            var container = pod.Spec.Containers.FirstOrDefault()?.Name ?? throw new InvalidOperationException("No containers found in the pod.");

            var logStream = await kubeClient.CoreV1.ReadNamespacedPodLogAsync(
                name,
                ns ?? _defaultNamespace,
                container: container,
                tailLines: tailLines,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return logStream;
        }
    }
}
