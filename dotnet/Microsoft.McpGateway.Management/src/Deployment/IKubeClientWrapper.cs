// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using k8s.Models;

namespace Microsoft.McpGateway.Management.Deployment
{
    /// <summary>
    /// Interface that defines a wrapper around Kubernetes client operations for managing services, stateful sets, deployments, and pods.
    /// </summary>
    public interface IKubeClientWrapper
    {
        /// <summary>
        /// Deletes a Kubernetes Service by name.
        /// </summary>
        Task<V1Service> DeleteServiceAsync(string name, string? ns = null, CancellationToken cancellationToken = default);

        /// <summary>
        /// Deletes a Kubernetes StatefulSet by name.
        /// </summary>
        Task<V1Status> DeleteStatefulSetAsync(string name, string? ns = null, CancellationToken cancellationToken = default);

        /// <summary>
        /// Retrieves a stream of logs from a container in a pod.
        /// </summary>
        /// <param name="name">The name of the pod.</param>
        /// <param name="tailLines">Number of log lines to retrieve from the end.</param>
        Task<Stream> GetContainerLogStream(string name, int tailLines = 1000, string? ns = null, CancellationToken cancellationToken = default);

        /// <summary>
        /// Lists all StatefulSets in the specified namespace.
        /// </summary>
        Task<V1StatefulSetList> ListStatefulSetsAsync(string? ns = null, CancellationToken cancellationToken = default);

        /// <summary>
        /// Applies a JSON merge or strategic merge patch to an existing StatefulSet.
        /// </summary>
        Task<V1StatefulSet> PatchStatefulSetAsync(V1Patch patch, string statefulSetName, string? ns = null, CancellationToken ct = default);

        /// <summary>
        /// Reads a Kubernetes Deployment by name.
        /// </summary>
        Task<V1Deployment> ReadDeploymentAsync(string name, string? ns = null, CancellationToken cancellationToken = default);

        /// <summary>
        /// Reads a Kubernetes Pod by name.
        /// </summary>
        Task<V1Pod> ReadPodAsync(string name, string? ns = null, CancellationToken cancellationToken = default);

        /// <summary>
        /// Reads a Kubernetes StatefulSet by name.
        /// </summary>
        Task<V1StatefulSet> ReadStatefulSetAsync(string name, string? ns = null, CancellationToken cancellationToken = default);

        /// <summary>
        /// Replaces an existing StatefulSet with a new definition.
        /// </summary>
        Task<V1StatefulSet> ReplaceStatefulSetAsync(V1StatefulSet statefulSet, string statefulSetName, string? ns = null, CancellationToken ct = default);

        /// <summary>
        /// Creates or updates a Kubernetes Service.
        /// </summary>
        Task<V1Service> UpsertServiceAsync(V1Service service, string? ns = null, CancellationToken ct = default);

        /// <summary>
        /// Creates or updates a Kubernetes StatefulSet.
        /// </summary>
        Task<V1StatefulSet> UpsertStatefulSetAsync(V1StatefulSet statefulSet, string? ns = null, CancellationToken cancellationToken = default);
    }
}
