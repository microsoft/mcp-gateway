// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.McpGateway.Management.Contracts;
using Microsoft.McpGateway.Management.Extensions;

namespace Microsoft.McpGateway.Management.Deployment
{
    public class KubernetesAdapterDeploymentManager : IAdapterDeploymentManager
    {
        private const string AdapterNamespace = "adapter";
        private const string TypeLabel = $"{AdapterNamespace}/type";
        private const string NameLabel = $"{AdapterNamespace}/name";
        private const string CreationAttemptAnnotation = $"{AdapterNamespace}/creation-attempt";
        // Name of the first-party tool gateway StatefulSet.
        private const string ToolGatewayName = "toolgateway";
        private const string NameInUseMessage = "A Kubernetes resource with the same name already exists.";
        private const string NotOwnedMessage = "The deployment does not belong to this resource.";
        private static readonly Regex ValidImageNamePattern = new(@"^[a-z0-9]+([._/-][a-z0-9]+)*$", RegexOptions.Compiled);
        private static readonly Regex ValidImageVersionPattern = new(@"^[a-zA-Z0-9][a-zA-Z0-9._-]{0,127}$", RegexOptions.Compiled);
        private readonly IKubeClientWrapper _kubeClient;
        private readonly string _containerRegistryAddress;
        private readonly ILogger<KubernetesAdapterDeploymentManager> _logger;
        private readonly string _namespace;
        private readonly DeploymentOwnership? _ownership;

        public KubernetesAdapterDeploymentManager(string containerRegistryAddress, IKubeClientWrapper kubeClient, ILogger<KubernetesAdapterDeploymentManager> logger, IOptions<GatewayKubernetesOptions>? options = null, DeploymentOwnership? ownership = null)
        {
            ArgumentException.ThrowIfNullOrEmpty(containerRegistryAddress);

            _containerRegistryAddress = containerRegistryAddress;
            _kubeClient = kubeClient ?? throw new ArgumentNullException(nameof(kubeClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _namespace = options?.Value.Namespace ?? "adapter";
            _ownership = ownership;
        }

        public async Task CreateDeploymentAsync(AdapterData request, ResourceType resourceType, CancellationToken cancellationToken)
        {
            ValidateImageReference(request.ImageName, request.ImageVersion);

            if (string.Equals(request.Name, ToolGatewayName, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The name is reserved.");

            var labels = new Dictionary<string, string>
            {
                { TypeLabel, ToLabelValue(resourceType) },
                { NameLabel, request.Name },
                { "azure.workload.identity/use", request.UseWorkloadIdentity.ToString().ToLowerInvariant() }
            };
            var attemptId = Guid.NewGuid().ToString("N");

            var statefulSet = new V1StatefulSet
            {
                Metadata = new V1ObjectMeta { Name = request.Name, Annotations = new Dictionary<string, string> { [CreationAttemptAnnotation] = attemptId } },
                Spec = new V1StatefulSetSpec
                {
                    ServiceName = $"{request.Name}-service",
                    Replicas = request.ReplicaCount,
                    Selector = new V1LabelSelector { MatchLabels = labels },
                    Template = new V1PodTemplateSpec
                    {
                        Metadata = new V1ObjectMeta { Labels = labels },
                        Spec = new V1PodSpec
                        {
                            ServiceAccountName = "workload-sa",
                            SecurityContext = new V1PodSecurityContext
                            {
                                RunAsUser = 1100,
                                RunAsGroup = 1100
                            },
                            Containers =
                            [
                                new()
                                {
                                    Name = $"{request.Name}-container",
                                    Image = $"{_containerRegistryAddress}/{request.ImageName}:{request.ImageVersion}",
                                    ImagePullPolicy = "Always",
                                    Env = [.. request.EnvironmentVariables?.Select(x => new V1EnvVar{ Name = x.Key, Value = x.Value }) ?? []],
                                    Ports =
                                    [
                                        new V1ContainerPort
                                        {
                                            ContainerPort = 8000,
                                            Protocol = "TCP"
                                        }
                                    ],
                                    SecurityContext = new V1SecurityContext
                                    {
                                        AllowPrivilegeEscalation = false,
                                        Capabilities = new V1Capabilities { Drop = ["ALL"] }
                                    },
                                    Resources = new V1ResourceRequirements
                                    {
                                        Limits = new Dictionary<string, ResourceQuantity>
                                        {
                                            ["cpu"] = new ResourceQuantity("1"),
                                            ["memory"] = new ResourceQuantity("512Mi"),
                                            ["ephemeral-storage"] = new ResourceQuantity("2Gi")
                                        },
                                        Requests = new Dictionary<string, ResourceQuantity>
                                        {
                                            ["cpu"] = new ResourceQuantity("250m"),
                                            ["memory"] = new ResourceQuantity("256Mi")
                                        }
                                    }
                                }
                            ]
                        }
                    }
                }
            };

            // For tools: use ClusterIP (default, stateless routing)
            // For adapters: use headless service (ClusterIP = None, stateful routing)
            var service = new V1Service
            {
                Metadata = new V1ObjectMeta
                {
                    Name = $"{request.Name}-service",
                    Annotations = new Dictionary<string, string> { [CreationAttemptAnnotation] = attemptId }
                },
                Spec = new V1ServiceSpec
                {
                    ClusterIP = resourceType == ResourceType.Tool ? null : "None",
                    Selector = labels,
                    Ports =
                    [
                        new()
                        {
                            Port = 8000,
                            TargetPort = 8000,
                            Protocol = "TCP"
                        }
                    ]
                }
            };

            _logger.LogInformation("Creating deployment for {name} with resource type {resourceType}.", request.Name.Sanitize(), resourceType.ToString().ToLowerInvariant());

            // Creation is the atomic name reservation: never adopt an existing object, which may belong to another owner or resource type.
            _ownership?.Invalidate(request.Name);
            try
            {
                await _kubeClient.UpsertStatefulSetAsync(statefulSet, _namespace, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Submitted Kubernetes deployment for {name}.", request.Name.Sanitize());
                await _kubeClient.UpsertServiceAsync(service, _namespace, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Submitted Kubernetes service for {name} with {serviceType} routing.", request.Name.Sanitize(), resourceType == ResourceType.Tool ? "stateless (ClusterIP)" : "stateful (headless)");
            }
            catch (Exception ex)
            {
                await RemoveCreationAttemptAsync(request.Name, attemptId).ConfigureAwait(false);

                if (ex is HttpOperationException { Response.StatusCode: HttpStatusCode.Conflict })
                {
                    if (await IsDeletionPendingAsync(request.Name).ConfigureAwait(false))
                        throw new DeploymentDeletionPendingException("A previous deployment with the same name is still being deleted. Retry shortly.");

                    _logger.LogWarning("Kubernetes resource for {name} already exists. Refusing to create.", request.Name.Sanitize());
                    throw new ArgumentException(NameInUseMessage);
                }

                throw;
            }
            finally
            {
                _ownership?.Invalidate(request.Name);
            }
        }

        private async Task<bool> IsDeletionPendingAsync(string name)
        {
            try
            {
                var statefulSet = await IgnoreNotFoundAsync(() => _kubeClient.ReadStatefulSetAsync(name, _namespace, CancellationToken.None)).ConfigureAwait(false);
                var service = await IgnoreNotFoundAsync(() => _kubeClient.ReadServiceAsync($"{name}-service", _namespace, CancellationToken.None)).ConfigureAwait(false);
                return statefulSet?.Metadata?.DeletionTimestamp != null || service?.Metadata?.DeletionTimestamp != null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not check deletion state for {name}.", name.Sanitize());
                return false;
            }
        }

        // A failed or interrupted create may still have persisted objects; remove only those carrying this attempt's marker.
        private async Task RemoveCreationAttemptAsync(string name, string attemptId)
        {
            try
            {
                var service = await IgnoreNotFoundAsync(() => _kubeClient.ReadServiceAsync($"{name}-service", _namespace, CancellationToken.None)).ConfigureAwait(false);
                if (IsCreationAttempt(service?.Metadata, attemptId))
                    await IgnoreNotFoundAsync(() => _kubeClient.DeleteServiceAsync($"{name}-service", service!.Metadata.Uid, _namespace, CancellationToken.None)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to roll back Kubernetes service for {name}.", name.Sanitize());
            }

            try
            {
                var statefulSet = await IgnoreNotFoundAsync(() => _kubeClient.ReadStatefulSetAsync(name, _namespace, CancellationToken.None)).ConfigureAwait(false);
                if (IsCreationAttempt(statefulSet?.Metadata, attemptId))
                    await IgnoreNotFoundAsync(() => _kubeClient.DeleteStatefulSetAsync(name, statefulSet!.Metadata.Uid, _namespace, CancellationToken.None)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to roll back Kubernetes deployment for {name}.", name.Sanitize());
            }
        }

        private static bool IsCreationAttempt(V1ObjectMeta? metadata, string attemptId) =>
            !string.IsNullOrEmpty(metadata?.Uid) &&
            metadata.Annotations?.TryGetValue(CreationAttemptAnnotation, out var value) == true && value == attemptId;

        public async Task UpdateDeploymentAsync(AdapterData request, ResourceType resourceType, CancellationToken cancellationToken)
        {
            ValidateImageReference(request.ImageName, request.ImageVersion);

            var statefulSet = await ReadOwnedStatefulSetAsync(request.Name, resourceType, cancellationToken).ConfigureAwait(false);

            var patch = new
            {
                metadata = new
                {
                    uid = RequireUid(statefulSet.Metadata)
                },
                spec = new
                {
                    replicas = request.ReplicaCount,
                    template = new
                    {
                        metadata = new
                        {
                            labels = new Dictionary<string, string>
                            {
                                { TypeLabel, ToLabelValue(resourceType) },
                                { NameLabel, request.Name },
                                { "azure.workload.identity/use", request.UseWorkloadIdentity.ToString().ToLowerInvariant() }
                            }
                        },
                        spec = new
                        {
                            containers = new[]
                            {
                                new
                                {
                                    name = $"{request.Name}-container",
                                    image = $"{_containerRegistryAddress}/{request.ImageName}:{request.ImageVersion}",
                                    env = request.EnvironmentVariables.Select(x => new V1EnvVar{ Name = x.Key, Value = x.Value }).ToArray(),
                                }
                            }
                        }
                    }
                }
            };

            var patchContent = new V1Patch(JsonSerializer.Serialize(patch), V1Patch.PatchType.StrategicMergePatch);
            _logger.LogInformation("Updating deployment for {name} with resource type {resourceType}.", request.Name.Sanitize(), resourceType.ToString().ToLowerInvariant());
            await _kubeClient.PatchStatefulSetAsync(patchContent, request.Name, _namespace, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Submitted updating deployment for {name}.", request.Name.Sanitize());
        }

        public async Task DeleteDeploymentAsync(string name, ResourceType resourceType, CancellationToken cancellationToken)
        {
            _ownership?.Invalidate(name);
            _logger.LogInformation("Deleting deployment for {name}.", name.Sanitize());

            var statefulSet = await IgnoreNotFoundAsync(() => _kubeClient.ReadStatefulSetAsync(name, _namespace, cancellationToken)).ConfigureAwait(false);
            if (statefulSet == null)
            {
                _logger.LogWarning("Deployment for {name} does not exist in the cluster.", name.Sanitize());
            }
            else if (IsOwnedBy(statefulSet.Spec?.Selector?.MatchLabels, name, resourceType))
            {
                await IgnoreNotFoundAsync(() => _kubeClient.DeleteStatefulSetAsync(name, RequireUid(statefulSet.Metadata), _namespace, cancellationToken)).ConfigureAwait(false);
                _logger.LogInformation("Submitted deleting deployment for {name}.", name.Sanitize());
            }
            else
            {
                _logger.LogWarning("Skipped deleting deployment {name}: it does not belong to the {resourceType} resource.", name.Sanitize(), ToLabelValue(resourceType));
            }

            var serviceName = $"{name}-service";
            var service = await IgnoreNotFoundAsync(() => _kubeClient.ReadServiceAsync(serviceName, _namespace, cancellationToken)).ConfigureAwait(false);
            if (service == null)
            {
                _logger.LogWarning("Service for {name} does not exist in the cluster.", name.Sanitize());
            }
            else if (IsOwnedBy(service.Spec?.Selector, name, resourceType))
            {
                await IgnoreNotFoundAsync(() => _kubeClient.DeleteServiceAsync(serviceName, RequireUid(service.Metadata), _namespace, cancellationToken)).ConfigureAwait(false);
                _logger.LogInformation("Submitted deleting service for {name}.", name.Sanitize());
            }
            else
            {
                _logger.LogWarning("Skipped deleting service for {name}: it does not belong to the {resourceType} resource.", name.Sanitize(), ToLabelValue(resourceType));
            }
            _ownership?.Invalidate(name);
        }

        public async Task<AdapterStatus> GetDeploymentStatusAsync(string name, ResourceType resourceType, CancellationToken cancellationToken)
        {
            var statefulSet = await ReadOwnedStatefulSetAsync(name, resourceType, cancellationToken).ConfigureAwait(false);
            var status = new AdapterStatus
            {
                ReadyReplicas = statefulSet.Status.ReadyReplicas,
                UpdatedReplicas = statefulSet.Status.UpdatedReplicas,
                AvailableReplicas = statefulSet.Status.AvailableReplicas,
                Image = statefulSet.Spec.Template.Spec.Containers.FirstOrDefault()?.Image ?? "Unknown"
            };

            if ((status.ReadyReplicas ?? 0) == (statefulSet.Spec.Replicas ?? 0))
                status.ReplicaStatus = "Healthy";
            else
                status.ReplicaStatus = $"Degraded: {status.ReadyReplicas ?? 0}/{statefulSet.Spec.Replicas ?? 0} ready";

            return status;
        }

        public async Task<string> GetDeploymentLogsAsync(string name, ResourceType resourceType, int ordinal = 0, CancellationToken cancellationToken = default)
        {
            var statefulSet = await ReadOwnedStatefulSetAsync(name, resourceType, cancellationToken).ConfigureAwait(false);
            var podName = $"{name}-{ordinal}";

            // A same-named pod can outlive a deleted StatefulSet, so require the current StatefulSet as its owner.
            var pod = await _kubeClient.ReadPodAsync(podName, _namespace, cancellationToken).ConfigureAwait(false);
            var ownerUid = statefulSet.Metadata?.Uid;
            if (string.IsNullOrEmpty(ownerUid) || pod?.Metadata?.OwnerReferences?.Any(o => o.Kind == "StatefulSet" && o.Uid == ownerUid) != true)
            {
                _logger.LogWarning("Pod {podName} does not belong to deployment {name}.", podName.Sanitize(), name.Sanitize());
                throw new UnauthorizedAccessException(NotOwnedMessage);
            }

            using var logStream = await _kubeClient.GetContainerLogStream(podName, 1000, _namespace, cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(logStream);
            var logText = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            return logText;
        }

        private async Task<V1StatefulSet> ReadOwnedStatefulSetAsync(string name, ResourceType resourceType, CancellationToken cancellationToken)
        {
            var statefulSet = await _kubeClient.ReadStatefulSetAsync(name, _namespace, cancellationToken).ConfigureAwait(false);
            if (statefulSet != null && IsOwnedBy(statefulSet.Spec?.Selector?.MatchLabels, name, resourceType))
                return statefulSet;

            _logger.LogWarning("Deployment {name} does not belong to the {resourceType} resource.", name.Sanitize(), ToLabelValue(resourceType));
            throw new UnauthorizedAccessException(NotOwnedMessage);
        }

        // Selector labels are set at creation (immutable on StatefulSets) and identify the owning resource.
        private static bool IsOwnedBy(IDictionary<string, string>? selector, string name, ResourceType resourceType) =>
            DeploymentOwnership.Matches(selector, name, resourceType);

        private static string RequireUid(V1ObjectMeta? metadata) =>
            !string.IsNullOrEmpty(metadata?.Uid) ? metadata.Uid : throw new UnauthorizedAccessException(NotOwnedMessage);

        private static string ToLabelValue(ResourceType resourceType) => resourceType.ToString().ToLowerInvariant();

        private static async Task<T?> IgnoreNotFoundAsync<T>(Func<Task<T>> operation) where T : class
        {
            try
            {
                return await operation().ConfigureAwait(false);
            }
            catch (HttpOperationException ex) when (ex.Response?.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }
        }

        private static void ValidateImageReference(string imageName, string imageVersion)
        {
            ArgumentException.ThrowIfNullOrEmpty(imageName);
            ArgumentException.ThrowIfNullOrEmpty(imageVersion);

            if (imageName.Contains("..", StringComparison.Ordinal))
                throw new ArgumentException("ImageName must not contain path traversal sequences.", nameof(imageName));

            if (!ValidImageNamePattern.IsMatch(imageName))
                throw new ArgumentException("ImageName contains invalid characters. Only lowercase alphanumeric, dots, dashes, and slashes are allowed.", nameof(imageName));

            if (!ValidImageVersionPattern.IsMatch(imageVersion))
                throw new ArgumentException("ImageVersion contains invalid characters.", nameof(imageVersion));
        }
    }
}
