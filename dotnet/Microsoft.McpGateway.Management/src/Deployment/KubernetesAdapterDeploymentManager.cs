// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Text.Json;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.Logging;
using Microsoft.McpGateway.Management.Contracts;
using Microsoft.McpGateway.Management.Extensions;

namespace Microsoft.McpGateway.Management.Deployment
{
    public class KubernetesAdapterDeploymentManager : IAdapterDeploymentManager
    {
        private const string AdapterNamespace = "adapter";
        private readonly IKubeClientWrapper _kubeClient;
        private readonly string _containerRegistryAddress;
        private readonly ILogger<KubernetesAdapterDeploymentManager> _logger;

        public KubernetesAdapterDeploymentManager(string containerRegistryAddress, IKubeClientWrapper kubeClient, ILogger<KubernetesAdapterDeploymentManager> logger)
        {
            ArgumentException.ThrowIfNullOrEmpty(containerRegistryAddress);

            _containerRegistryAddress = containerRegistryAddress;
            _kubeClient = kubeClient ?? throw new ArgumentNullException(nameof(kubeClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task CreateDeploymentAsync(AdapterData request, CancellationToken cancellationToken)
        {
            var labels = new Dictionary<string, string>
            {
                { $"{AdapterNamespace}/type", "mcp" },
                { $"{AdapterNamespace}/name", request.Name },
                { "azure.workload.identity/use", request.UseWorkloadIdentity.ToString().ToLowerInvariant() }
            };

            var statefulSet = new V1StatefulSet
            {
                Metadata = new V1ObjectMeta { Name = request.Name },
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

            var service = new V1Service
            {
                Metadata = new V1ObjectMeta
                {
                    Name = $"{request.Name}-service"
                },
                Spec = new V1ServiceSpec
                {
                    ClusterIP = "None",
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

            _logger.LogInformation("Creating deployment for {name}.", request.Name.Sanitize());
            try
            {
                await _kubeClient.UpsertStatefulSetAsync(statefulSet, AdapterNamespace, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Submitted Kubernetes deployment for {name}.", request.Name.Sanitize());
            }
            catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.Conflict)
            {
                _logger.LogInformation("Kubernetes deployment for {name} already exists. Skip deployment.", request.Name.Sanitize());
            }

            try
            {
                await _kubeClient.UpsertServiceAsync(service, AdapterNamespace, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Submitted Kubernetes service for {name}.", request.Name.Sanitize());
            }
            catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.Conflict)
            {
                _logger.LogInformation("Kubernetes service for {name} already exists. Skip service creation.", request.Name.Sanitize());
            }
        }

        public async Task UpdateDeploymentAsync(AdapterData request, CancellationToken cancellationToken)
        {
            var statefulSet = await _kubeClient.ReadStatefulSetAsync(request.Name, AdapterNamespace, cancellationToken).ConfigureAwait(false);
            var patch = new
            {
                spec = new
                {
                    replicas = request.ReplicaCount,
                    template = new
                    {
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
            _logger.LogInformation("Updating deployment for {name}.", request.Name.Sanitize());
            await _kubeClient.PatchStatefulSetAsync(patchContent, request.Name, AdapterNamespace, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Submitted updating deployment for {name}.", request.Name.Sanitize());
        }

        public async Task DeleteDeploymentAsync(string name, CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogInformation("Deleting deployment for {name}.", name.Sanitize());
                await _kubeClient.DeleteStatefulSetAsync(name, AdapterNamespace, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Submitted deleting deployment for {name}.", name.Sanitize());
                await _kubeClient.DeleteServiceAsync($"{name}-service", AdapterNamespace, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Submitted deleting service for {name}.", name.Sanitize());
            }
            catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound)
            {
                _logger.LogWarning("Deployment for {name} does not exist in the cluster.", name.Sanitize());
            }
        }

        public async Task<AdapterStatus> GetDeploymentStatusAsync(string name, CancellationToken cancellationToken)
        {
            var statefulSet = await _kubeClient.ReadStatefulSetAsync(name, AdapterNamespace, cancellationToken).ConfigureAwait(false);
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

        public async Task<string> GetDeploymentLogsAsync(string name, int ordinal = 0, CancellationToken cancellationToken = default)
        {
            var podName = $"{name}-{ordinal}";
            using var logStream = await _kubeClient.GetContainerLogStream(podName, 1000, AdapterNamespace, cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(logStream);
            var logText = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            return logText;
        }
    }
}
