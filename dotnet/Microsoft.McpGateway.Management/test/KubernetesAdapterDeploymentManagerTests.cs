// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using k8s.Models;
using Microsoft.Extensions.Logging;
using Microsoft.McpGateway.Management.Contracts;
using Microsoft.McpGateway.Management.Deployment;
using Moq;

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

            await _manager.CreateDeploymentAsync(request, CancellationToken.None);

            _kubeClientMock.Verify(x => x.UpsertStatefulSetAsync(It.IsAny<V1StatefulSet>(), "adapter", It.IsAny<CancellationToken>()), Times.Once);
            _kubeClientMock.Verify(x => x.UpsertServiceAsync(It.IsAny<V1Service>(), "adapter", It.IsAny<CancellationToken>()), Times.Once);
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

            await _manager.UpdateDeploymentAsync(request, CancellationToken.None);

            _kubeClientMock.Verify(x => x.PatchStatefulSetAsync(It.IsAny<V1Patch>(), "adapter1", "adapter", It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task DeleteDeploymentAsync_ShouldDeleteResources()
        {
            await _manager.DeleteDeploymentAsync("adapter1", CancellationToken.None);

            _kubeClientMock.Verify(x => x.DeleteStatefulSetAsync("adapter1", "adapter", It.IsAny<CancellationToken>()), Times.Once);
            _kubeClientMock.Verify(x => x.DeleteServiceAsync("adapter1-service", "adapter", It.IsAny<CancellationToken>()), Times.Once);
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
    }
}
