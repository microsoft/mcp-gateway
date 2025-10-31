// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using k8s.Models;
using Microsoft.Extensions.Logging;
using Microsoft.McpGateway.Management.Contracts;
using Microsoft.McpGateway.Management.Deployment;
using Moq;
using System.Net;
using k8s.Autorest;

namespace Microsoft.McpGateway.Management.Tests
{
    [TestClass]
    public class KubernetesAdapterDeploymentManagerTests
    {
        private readonly Mock<IKubeClientWrapper> _kubeClientMock;
        private readonly Mock<ILogger<KubernetesAdapterDeploymentManager>> _loggerMock;
        private readonly KubernetesAdapterDeploymentManager _manager;

        public KubernetesAdapterDeploymentManagerTests()
        {
            _kubeClientMock = new Mock<IKubeClientWrapper>();
            _loggerMock = new Mock<ILogger<KubernetesAdapterDeploymentManager>>();
            _manager = new KubernetesAdapterDeploymentManager("registry.io", _kubeClientMock.Object, _loggerMock.Object);
        }

        [TestMethod]
        public async Task CreateDeploymentAsync_ShouldCallUpsertMethods()
        {
            var request = new AdapterData
            {
                Name = "adapter1",
                ImageName = "image",
                ImageVersion = "v1",
                ReplicaCount = 1,
                EnvironmentVariables = new Dictionary<string, string> { { "ENV", "value" } }
            };

            await _manager.CreateDeploymentAsync(request, ResourceType.Mcp, CancellationToken.None);

            _kubeClientMock.Verify(x => x.UpsertStatefulSetAsync(It.IsAny<V1StatefulSet>(), "adapter", It.IsAny<CancellationToken>()), Times.Once);
            _kubeClientMock.Verify(x => x.UpsertServiceAsync(It.IsAny<V1Service>(), "adapter", It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task CreateDeploymentAsync_WithToolResourceType_ShouldCreateClusterIPService()
        {
            var request = new AdapterData
            {
                Name = "tool1",
                ImageName = "tool-image",
                ImageVersion = "v1",
                ReplicaCount = 1
            };

            V1Service? capturedService = null;
            _kubeClientMock.Setup(x => x.UpsertServiceAsync(It.IsAny<V1Service>(), "adapter", It.IsAny<CancellationToken>()))
                .Callback<V1Service, string, CancellationToken>((service, ns, ct) => capturedService = service)
                .ReturnsAsync(new V1Service());

            await _manager.CreateDeploymentAsync(request, ResourceType.Tool, CancellationToken.None);

            capturedService.Should().NotBeNull();
            capturedService!.Spec.ClusterIP.Should().BeNull(); // ClusterIP service (not headless)
        }

        [TestMethod]
        public async Task CreateDeploymentAsync_WithMcpResourceType_ShouldCreateHeadlessService()
        {
            var request = new AdapterData
            {
                Name = "adapter1",
                ImageName = "adapter-image",
                ImageVersion = "v1",
                ReplicaCount = 1
            };

            V1Service? capturedService = null;
            _kubeClientMock.Setup(x => x.UpsertServiceAsync(It.IsAny<V1Service>(), "adapter", It.IsAny<CancellationToken>()))
                .Callback<V1Service, string, CancellationToken>((service, ns, ct) => capturedService = service)
                .ReturnsAsync(new V1Service());

            await _manager.CreateDeploymentAsync(request, ResourceType.Mcp, CancellationToken.None);

            capturedService.Should().NotBeNull();
            capturedService!.Spec.ClusterIP.Should().Be("None"); // Headless service
        }

        [TestMethod]
        public async Task CreateDeploymentAsync_WithUseWorkloadIdentityTrue_ShouldSetLabelCorrectly()
        {
            var request = new AdapterData
            {
                Name = "adapter1",
                ImageName = "image",
                ImageVersion = "v1",
                ReplicaCount = 1,
                UseWorkloadIdentity = true
            };

            V1StatefulSet? capturedStatefulSet = null;
            _kubeClientMock.Setup(x => x.UpsertStatefulSetAsync(It.IsAny<V1StatefulSet>(), "adapter", It.IsAny<CancellationToken>()))
                .Callback<V1StatefulSet, string, CancellationToken>((ss, ns, ct) => capturedStatefulSet = ss)
                .ReturnsAsync(new V1StatefulSet());

            await _manager.CreateDeploymentAsync(request, ResourceType.Mcp, CancellationToken.None);

            capturedStatefulSet.Should().NotBeNull();
            capturedStatefulSet!.Spec.Template.Metadata.Labels["azure.workload.identity/use"].Should().Be("true");
        }

        [TestMethod]
        public async Task CreateDeploymentAsync_WithUseWorkloadIdentityFalse_ShouldSetLabelCorrectly()
        {
            var request = new AdapterData
            {
                Name = "adapter1",
                ImageName = "image",
                ImageVersion = "v1",
                ReplicaCount = 1,
                UseWorkloadIdentity = false
            };

            V1StatefulSet? capturedStatefulSet = null;
            _kubeClientMock.Setup(x => x.UpsertStatefulSetAsync(It.IsAny<V1StatefulSet>(), "adapter", It.IsAny<CancellationToken>()))
                .Callback<V1StatefulSet, string, CancellationToken>((ss, ns, ct) => capturedStatefulSet = ss)
                .ReturnsAsync(new V1StatefulSet());

            await _manager.CreateDeploymentAsync(request, ResourceType.Mcp, CancellationToken.None);

            capturedStatefulSet.Should().NotBeNull();
            capturedStatefulSet!.Spec.Template.Metadata.Labels["azure.workload.identity/use"].Should().Be("false");
        }

        [TestMethod]
        public async Task CreateDeploymentAsync_WithResourceTypeTool_ShouldSetCorrectLabel()
        {
            var request = new AdapterData
            {
                Name = "tool1",
                ImageName = "image",
                ImageVersion = "v1",
                ReplicaCount = 1
            };

            V1StatefulSet? capturedStatefulSet = null;
            _kubeClientMock.Setup(x => x.UpsertStatefulSetAsync(It.IsAny<V1StatefulSet>(), "adapter", It.IsAny<CancellationToken>()))
                .Callback<V1StatefulSet, string, CancellationToken>((ss, ns, ct) => capturedStatefulSet = ss)
                .ReturnsAsync(new V1StatefulSet());

            await _manager.CreateDeploymentAsync(request, ResourceType.Tool, CancellationToken.None);

            capturedStatefulSet.Should().NotBeNull();
            capturedStatefulSet!.Spec.Template.Metadata.Labels["adapter/type"].Should().Be("tool");
        }

        [TestMethod]
        public async Task CreateDeploymentAsync_WithResourceTypeMcp_ShouldSetCorrectLabel()
        {
            var request = new AdapterData
            {
                Name = "adapter1",
                ImageName = "image",
                ImageVersion = "v1",
                ReplicaCount = 1
            };

            V1StatefulSet? capturedStatefulSet = null;
            _kubeClientMock.Setup(x => x.UpsertStatefulSetAsync(It.IsAny<V1StatefulSet>(), "adapter", It.IsAny<CancellationToken>()))
                .Callback<V1StatefulSet, string, CancellationToken>((ss, ns, ct) => capturedStatefulSet = ss)
                .ReturnsAsync(new V1StatefulSet());

            await _manager.CreateDeploymentAsync(request, ResourceType.Mcp, CancellationToken.None);

            capturedStatefulSet.Should().NotBeNull();
            capturedStatefulSet!.Spec.Template.Metadata.Labels["adapter/type"].Should().Be("mcp");
        }

        [TestMethod]
        public async Task CreateDeploymentAsync_WithNullEnvironmentVariables_ShouldNotThrow()
        {
            var request = new AdapterData
            {
                Name = "adapter1",
                ImageName = "image",
                ImageVersion = "v1",
                ReplicaCount = 1,
                EnvironmentVariables = null!
            };

            V1StatefulSet? capturedStatefulSet = null;
            _kubeClientMock.Setup(x => x.UpsertStatefulSetAsync(It.IsAny<V1StatefulSet>(), "adapter", It.IsAny<CancellationToken>()))
                .Callback<V1StatefulSet, string, CancellationToken>((ss, ns, ct) => capturedStatefulSet = ss)
                .ReturnsAsync(new V1StatefulSet());

            await _manager.CreateDeploymentAsync(request, ResourceType.Mcp, CancellationToken.None);

            capturedStatefulSet.Should().NotBeNull();
            capturedStatefulSet!.Spec.Template.Spec.Containers[0].Env.Should().BeEmpty();
        }

        [TestMethod]
        public async Task CreateDeploymentAsync_WithEmptyEnvironmentVariables_ShouldCreateEmptyEnvList()
        {
            var request = new AdapterData
            {
                Name = "adapter1",
                ImageName = "image",
                ImageVersion = "v1",
                ReplicaCount = 1,
                EnvironmentVariables = new Dictionary<string, string>()
            };

            V1StatefulSet? capturedStatefulSet = null;
            _kubeClientMock.Setup(x => x.UpsertStatefulSetAsync(It.IsAny<V1StatefulSet>(), "adapter", It.IsAny<CancellationToken>()))
                .Callback<V1StatefulSet, string, CancellationToken>((ss, ns, ct) => capturedStatefulSet = ss)
                .ReturnsAsync(new V1StatefulSet());

            await _manager.CreateDeploymentAsync(request, ResourceType.Mcp, CancellationToken.None);

            capturedStatefulSet.Should().NotBeNull();
            capturedStatefulSet!.Spec.Template.Spec.Containers[0].Env.Should().BeEmpty();
        }

        [TestMethod]
        public async Task CreateDeploymentAsync_WithMultipleEnvironmentVariables_ShouldSetAllEnvVars()
        {
            var request = new AdapterData
            {
                Name = "adapter1",
                ImageName = "image",
                ImageVersion = "v1",
                ReplicaCount = 1,
                EnvironmentVariables = new Dictionary<string, string>
                {
                    { "VAR1", "value1" },
                    { "VAR2", "value2" },
                    { "VAR3", "value3" }
                }
            };

            V1StatefulSet? capturedStatefulSet = null;
            _kubeClientMock.Setup(x => x.UpsertStatefulSetAsync(It.IsAny<V1StatefulSet>(), "adapter", It.IsAny<CancellationToken>()))
                .Callback<V1StatefulSet, string, CancellationToken>((ss, ns, ct) => capturedStatefulSet = ss)
                .ReturnsAsync(new V1StatefulSet());

            await _manager.CreateDeploymentAsync(request, ResourceType.Mcp, CancellationToken.None);

            capturedStatefulSet.Should().NotBeNull();
            var envVars = capturedStatefulSet!.Spec.Template.Spec.Containers[0].Env;
            envVars.Should().HaveCount(3);
            envVars.Should().Contain(e => e.Name == "VAR1" && e.Value == "value1");
            envVars.Should().Contain(e => e.Name == "VAR2" && e.Value == "value2");
            envVars.Should().Contain(e => e.Name == "VAR3" && e.Value == "value3");
        }

        [TestMethod]
        public async Task CreateDeploymentAsync_WhenStatefulSetExists_ShouldLogAndContinue()
        {
            var request = new AdapterData
            {
                Name = "adapter1",
                ImageName = "image",
                ImageVersion = "v1",
                ReplicaCount = 1
            };

            var conflictException = new HttpOperationException
            {
                Response = new HttpResponseMessageWrapper(
                    new HttpResponseMessage(HttpStatusCode.Conflict),
                    "Conflict")
            };

            _kubeClientMock.Setup(x => x.UpsertStatefulSetAsync(It.IsAny<V1StatefulSet>(), "adapter", It.IsAny<CancellationToken>()))
                .ThrowsAsync(conflictException);

            await _manager.CreateDeploymentAsync(request, ResourceType.Mcp, CancellationToken.None);

            _kubeClientMock.Verify(x => x.UpsertServiceAsync(It.IsAny<V1Service>(), "adapter", It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task CreateDeploymentAsync_WhenServiceExists_ShouldLogAndComplete()
        {
            var request = new AdapterData
            {
                Name = "adapter1",
                ImageName = "image",
                ImageVersion = "v1",
                ReplicaCount = 1
            };

            var conflictException = new HttpOperationException
            {
                Response = new HttpResponseMessageWrapper(
                    new HttpResponseMessage(HttpStatusCode.Conflict),
                    "Conflict")
            };

            _kubeClientMock.Setup(x => x.UpsertServiceAsync(It.IsAny<V1Service>(), "adapter", It.IsAny<CancellationToken>()))
                .ThrowsAsync(conflictException);

            await _manager.CreateDeploymentAsync(request, ResourceType.Mcp, CancellationToken.None);

            _kubeClientMock.Verify(x => x.UpsertStatefulSetAsync(It.IsAny<V1StatefulSet>(), "adapter", It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task UpdateDeploymentAsync_ShouldPatchStatefulSet()
        {
            var request = new AdapterData
            {
                Name = "adapter1",
                ImageName = "image",
                ImageVersion = "v2",
                ReplicaCount = 2,
                EnvironmentVariables = new Dictionary<string, string> { { "ENV", "value" } }
            };

            var statefulSet = new V1StatefulSet
            {
                Metadata = new V1ObjectMeta { Name = "adapter1" },
                Spec = new V1StatefulSetSpec { Replicas = 1 }
            };

            _kubeClientMock.Setup(x => x.ReadStatefulSetAsync("adapter1", "adapter", It.IsAny<CancellationToken>())).ReturnsAsync(statefulSet);

            await _manager.UpdateDeploymentAsync(request, ResourceType.Mcp, CancellationToken.None);

            _kubeClientMock.Verify(x => x.PatchStatefulSetAsync(It.IsAny<V1Patch>(), "adapter1", "adapter", It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task UpdateDeploymentAsync_WithToolResourceType_ShouldSetCorrectLabel()
        {
            var request = new AdapterData
            {
                Name = "tool1",
                ImageName = "image",
                ImageVersion = "v2",
                ReplicaCount = 2,
                EnvironmentVariables = new Dictionary<string, string>()
            };

            var statefulSet = new V1StatefulSet
            {
                Metadata = new V1ObjectMeta { Name = "tool1" },
                Spec = new V1StatefulSetSpec { Replicas = 1 }
            };

            _kubeClientMock.Setup(x => x.ReadStatefulSetAsync("tool1", "adapter", It.IsAny<CancellationToken>())).ReturnsAsync(statefulSet);

            V1Patch? capturedPatch = null;
            _kubeClientMock.Setup(x => x.PatchStatefulSetAsync(It.IsAny<V1Patch>(), "tool1", "adapter", It.IsAny<CancellationToken>()))
                .Callback<V1Patch, string, string, CancellationToken>((patch, name, ns, ct) => capturedPatch = patch)
                .ReturnsAsync(new V1StatefulSet());

            await _manager.UpdateDeploymentAsync(request, ResourceType.Tool, CancellationToken.None);

            capturedPatch.Should().NotBeNull();
            var content = capturedPatch!.Content as string;
            content.Should().NotBeNull();
            content.Should().Contain("\"adapter/type\":\"tool\"");
        }

        [TestMethod]
        public async Task UpdateDeploymentAsync_WithMcpResourceType_ShouldSetCorrectLabel()
        {
            var request = new AdapterData
            {
                Name = "adapter1",
                ImageName = "image",
                ImageVersion = "v2",
                ReplicaCount = 2,
                EnvironmentVariables = new Dictionary<string, string>()
            };

            var statefulSet = new V1StatefulSet
            {
                Metadata = new V1ObjectMeta { Name = "adapter1" },
                Spec = new V1StatefulSetSpec { Replicas = 1 }
            };

            _kubeClientMock.Setup(x => x.ReadStatefulSetAsync("adapter1", "adapter", It.IsAny<CancellationToken>())).ReturnsAsync(statefulSet);

            V1Patch? capturedPatch = null;
            _kubeClientMock.Setup(x => x.PatchStatefulSetAsync(It.IsAny<V1Patch>(), "adapter1", "adapter", It.IsAny<CancellationToken>()))
                .Callback<V1Patch, string, string, CancellationToken>((patch, name, ns, ct) => capturedPatch = patch)
                .ReturnsAsync(new V1StatefulSet());

            await _manager.UpdateDeploymentAsync(request, ResourceType.Mcp, CancellationToken.None);

            capturedPatch.Should().NotBeNull();
            var content = capturedPatch!.Content as string;
            content.Should().NotBeNull();
            content.Should().Contain("\"adapter/type\":\"mcp\"");
        }

        [TestMethod]
        public async Task DeleteDeploymentAsync_ShouldDeleteResources()
        {
            await _manager.DeleteDeploymentAsync("adapter1", CancellationToken.None);

            _kubeClientMock.Verify(x => x.DeleteStatefulSetAsync("adapter1", "adapter", It.IsAny<CancellationToken>()), Times.Once);
            _kubeClientMock.Verify(x => x.DeleteServiceAsync("adapter1-service", "adapter", It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task DeleteDeploymentAsync_WhenNotFound_ShouldLogWarningAndComplete()
        {
            var notFoundException = new HttpOperationException
            {
                Response = new HttpResponseMessageWrapper(
                    new HttpResponseMessage(HttpStatusCode.NotFound),
                    "Not Found")
            };

            _kubeClientMock.Setup(x => x.DeleteStatefulSetAsync("adapter1", "adapter", It.IsAny<CancellationToken>()))
                .ThrowsAsync(notFoundException);

            await _manager.DeleteDeploymentAsync("adapter1", CancellationToken.None);

            _kubeClientMock.Verify(x => x.DeleteStatefulSetAsync("adapter1", "adapter", It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task GetDeploymentStatusAsync_ShouldReturnCorrectStatus()
        {
            var statefulSet = new V1StatefulSet
            {
                Metadata = new V1ObjectMeta { Name = "adapter1" },
                Spec = new V1StatefulSetSpec
                {
                    Replicas = 2,
                    Template = new V1PodTemplateSpec
                    {
                        Spec = new V1PodSpec
                        {
                            Containers =
                            [
                                new V1Container { Image = "registry.io/image:v1" }
                            ]
                        }
                    }
                },
                Status = new V1StatefulSetStatus
                {
                    ReadyReplicas = 2,
                    UpdatedReplicas = 2,
                    AvailableReplicas = 2
                }
            };

            _kubeClientMock.Setup(x => x.ReadStatefulSetAsync("adapter1", "adapter", It.IsAny<CancellationToken>())).ReturnsAsync(statefulSet);

            var result = await _manager.GetDeploymentStatusAsync("adapter1", CancellationToken.None);

            result.ReadyReplicas.Should().Be(2);
            result.UpdatedReplicas.Should().Be(2);
            result.AvailableReplicas.Should().Be(2);
            result.Image.Should().Be("registry.io/image:v1");
            result.ReplicaStatus.Should().Be("Healthy");
        }

        [TestMethod]
        public async Task GetDeploymentStatusAsync_WhenDegraded_ShouldReturnDegradedStatus()
        {
            var statefulSet = new V1StatefulSet
            {
                Metadata = new V1ObjectMeta { Name = "adapter1" },
                Spec = new V1StatefulSetSpec
                {
                    Replicas = 3,
                    Template = new V1PodTemplateSpec
                    {
                        Spec = new V1PodSpec
                        {
                            Containers =
                            [
                                new V1Container { Image = "registry.io/image:v1" }
                            ]
                        }
                    }
                },
                Status = new V1StatefulSetStatus
                {
                    ReadyReplicas = 1,
                    UpdatedReplicas = 2,
                    AvailableReplicas = 1
                }
            };

            _kubeClientMock.Setup(x => x.ReadStatefulSetAsync("adapter1", "adapter", It.IsAny<CancellationToken>())).ReturnsAsync(statefulSet);

            var result = await _manager.GetDeploymentStatusAsync("adapter1", CancellationToken.None);

            result.ReadyReplicas.Should().Be(1);
            result.ReplicaStatus.Should().Be("Degraded: 1/3 ready");
        }

        [TestMethod]
        public async Task GetDeploymentStatusAsync_WhenNoReadyReplicas_ShouldReturnDegradedStatus()
        {
            var statefulSet = new V1StatefulSet
            {
                Metadata = new V1ObjectMeta { Name = "adapter1" },
                Spec = new V1StatefulSetSpec
                {
                    Replicas = 2,
                    Template = new V1PodTemplateSpec
                    {
                        Spec = new V1PodSpec
                        {
                            Containers =
                            [
                                new V1Container { Image = "registry.io/image:v1" }
                            ]
                        }
                    }
                },
                Status = new V1StatefulSetStatus
                {
                    ReadyReplicas = null,
                    UpdatedReplicas = 0,
                    AvailableReplicas = 0
                }
            };

            _kubeClientMock.Setup(x => x.ReadStatefulSetAsync("adapter1", "adapter", It.IsAny<CancellationToken>())).ReturnsAsync(statefulSet);

            var result = await _manager.GetDeploymentStatusAsync("adapter1", CancellationToken.None);

            result.ReadyReplicas.Should().BeNull();
            result.ReplicaStatus.Should().Be("Degraded: 0/2 ready");
        }

        [TestMethod]
        public async Task GetDeploymentStatusAsync_WhenNoContainers_ShouldReturnUnknownImage()
        {
            var statefulSet = new V1StatefulSet
            {
                Metadata = new V1ObjectMeta { Name = "adapter1" },
                Spec = new V1StatefulSetSpec
                {
                    Replicas = 1,
                    Template = new V1PodTemplateSpec
                    {
                        Spec = new V1PodSpec
                        {
                            Containers = []
                        }
                    }
                },
                Status = new V1StatefulSetStatus
                {
                    ReadyReplicas = 1,
                    UpdatedReplicas = 1,
                    AvailableReplicas = 1
                }
            };

            _kubeClientMock.Setup(x => x.ReadStatefulSetAsync("adapter1", "adapter", It.IsAny<CancellationToken>())).ReturnsAsync(statefulSet);

            var result = await _manager.GetDeploymentStatusAsync("adapter1", CancellationToken.None);

            result.Image.Should().Be("Unknown");
        }

        [TestMethod]
        public async Task GetDeploymentLogsAsync_ShouldReturnLogText()
        {
            var logStream = new MemoryStream();
            var writer = new StreamWriter(logStream);
            writer.Write("log-content");
            writer.Flush();
            logStream.Position = 0;

            _kubeClientMock.Setup(x => x.GetContainerLogStream("adapter1-0", 1000, "adapter", It.IsAny<CancellationToken>())).ReturnsAsync(logStream);

            var result = await _manager.GetDeploymentLogsAsync("adapter1", 0, CancellationToken.None);

            result.Should().Be("log-content");
        }

        [TestMethod]
        public async Task GetDeploymentLogsAsync_WithDifferentOrdinal_ShouldUseDifferentPodName()
        {
            var logStream = new MemoryStream();
            var writer = new StreamWriter(logStream);
            writer.Write("logs-from-pod-2");
            writer.Flush();
            logStream.Position = 0;

            _kubeClientMock.Setup(x => x.GetContainerLogStream("adapter1-2", 1000, "adapter", It.IsAny<CancellationToken>())).ReturnsAsync(logStream);

            var result = await _manager.GetDeploymentLogsAsync("adapter1", 2, CancellationToken.None);

            result.Should().Be("logs-from-pod-2");
            _kubeClientMock.Verify(x => x.GetContainerLogStream("adapter1-2", 1000, "adapter", It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public void Constructor_WithNullContainerRegistry_ShouldThrowArgumentException()
        {
            var act = () => new KubernetesAdapterDeploymentManager(null!, _kubeClientMock.Object, _loggerMock.Object);

            act.Should().Throw<ArgumentException>();
        }

        [TestMethod]
        public void Constructor_WithEmptyContainerRegistry_ShouldThrowArgumentException()
        {
            var act = () => new KubernetesAdapterDeploymentManager("", _kubeClientMock.Object, _loggerMock.Object);

            act.Should().Throw<ArgumentException>();
        }

        [TestMethod]
        public void Constructor_WithNullKubeClient_ShouldThrowArgumentNullException()
        {
            var act = () => new KubernetesAdapterDeploymentManager("registry.io", null!, _loggerMock.Object);

            act.Should().Throw<ArgumentNullException>().WithParameterName("kubeClient");
        }

        [TestMethod]
        public void Constructor_WithNullLogger_ShouldThrowArgumentNullException()
        {
            var act = () => new KubernetesAdapterDeploymentManager("registry.io", _kubeClientMock.Object, null!);

            act.Should().Throw<ArgumentNullException>().WithParameterName("logger");
        }
    }
}
