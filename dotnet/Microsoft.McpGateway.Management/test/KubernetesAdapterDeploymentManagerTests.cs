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
        private V1StatefulSet? _persistedStatefulSet;
        private V1Service? _persistedService;

        public KubernetesAdapterDeploymentManagerTests()
        {
            _kubeClientMock = new Mock<IKubeClientWrapper>();
            _kubeClientMock.Setup(client => client.UpsertStatefulSetAsync(It.IsAny<V1StatefulSet>(), "adapter", It.IsAny<CancellationToken>()))
                .ReturnsAsync((V1StatefulSet statefulSet, string namespaceName, CancellationToken cancellationToken) =>
                {
                    statefulSet.Metadata.Uid = "created-uid";
                    return _persistedStatefulSet = statefulSet;
                });
            _kubeClientMock.Setup(client => client.UpsertServiceAsync(It.IsAny<V1Service>(), "adapter", It.IsAny<CancellationToken>()))
                .ReturnsAsync((V1Service service, string namespaceName, CancellationToken cancellationToken) =>
                {
                    service.Metadata.Uid = "created-service-uid";
                    return _persistedService = service;
                });
            _kubeClientMock.Setup(client => client.ReadStatefulSetAsync(It.IsAny<string>(), "adapter", It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => _persistedStatefulSet ?? throw HttpError(HttpStatusCode.NotFound));
            _kubeClientMock.Setup(client => client.ReadServiceAsync(It.IsAny<string>(), "adapter", It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => _persistedService ?? throw HttpError(HttpStatusCode.NotFound));
            _loggerMock = new Mock<ILogger<KubernetesAdapterDeploymentManager>>();
            _manager = new KubernetesAdapterDeploymentManager("registry.io", _kubeClientMock.Object, _loggerMock.Object);
        }

        private static Dictionary<string, string> OwnerLabels(string name, string type) => new()
        {
            ["adapter/type"] = type,
            ["adapter/name"] = name,
            ["azure.workload.identity/use"] = "false"
        };

        private static V1StatefulSet OwnedStatefulSet(string name, string type = "mcp", string uid = "sts-uid") => new()
        {
            Metadata = new V1ObjectMeta { Name = name, Uid = uid, ResourceVersion = "10" },
            Spec = new V1StatefulSetSpec { Replicas = 1, Selector = new V1LabelSelector { MatchLabels = OwnerLabels(name, type) } }
        };

        private static V1Service OwnedService(string name, string type = "mcp") => new()
        {
            Metadata = new V1ObjectMeta { Name = $"{name}-service", Uid = "service-uid" },
            Spec = new V1ServiceSpec { Selector = OwnerLabels(name, type) }
        };

        private static V1Pod PodOwnedBy(string podName, string ownerName, string ownerUid) => new()
        {
            Metadata = new V1ObjectMeta
            {
                Name = podName,
                OwnerReferences = [new V1OwnerReference { Kind = "StatefulSet", Name = ownerName, Uid = ownerUid, Controller = true }]
            }
        };

        private static HttpOperationException HttpError(HttpStatusCode statusCode) => new()
        {
            Response = new HttpResponseMessageWrapper(new HttpResponseMessage(statusCode), statusCode.ToString())
        };

        private static MemoryStream LogStream(string text) => new(System.Text.Encoding.UTF8.GetBytes(text));

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
        public async Task CreateDeploymentAsync_WhenStatefulSetExists_ShouldThrowWithoutCreatingService()
        {
            var request = new AdapterData
            {
                Name = "adapter1",
                ImageName = "image",
                ImageVersion = "v1",
                ReplicaCount = 1
            };

            _kubeClientMock.Setup(x => x.UpsertStatefulSetAsync(It.IsAny<V1StatefulSet>(), "adapter", It.IsAny<CancellationToken>()))
                .ThrowsAsync(HttpError(HttpStatusCode.Conflict));

            Func<Task> act = () => _manager.CreateDeploymentAsync(request, ResourceType.Tool, CancellationToken.None);

            await act.Should().ThrowAsync<ArgumentException>().WithMessage("A Kubernetes resource with the same name already exists.");
            _kubeClientMock.Verify(x => x.UpsertServiceAsync(It.IsAny<V1Service>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
            _kubeClientMock.Verify(x => x.DeleteStatefulSetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [TestMethod]
        public async Task CreateDeploymentAsync_WhenServiceExists_ShouldRollBackStatefulSetAndThrow()
        {
            var request = new AdapterData
            {
                Name = "adapter1",
                ImageName = "image",
                ImageVersion = "v1",
                ReplicaCount = 1
            };

            _kubeClientMock.Setup(x => x.UpsertServiceAsync(It.IsAny<V1Service>(), "adapter", It.IsAny<CancellationToken>()))
                .ThrowsAsync(HttpError(HttpStatusCode.Conflict));

            Func<Task> act = () => _manager.CreateDeploymentAsync(request, ResourceType.Mcp, CancellationToken.None);

            await act.Should().ThrowAsync<ArgumentException>().WithMessage("A Kubernetes resource with the same name already exists.");
            _kubeClientMock.Verify(x => x.DeleteStatefulSetAsync("adapter1", "created-uid", "adapter", It.IsAny<CancellationToken>()), Times.Once);
            _kubeClientMock.Verify(x => x.DeleteServiceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [TestMethod]
        public async Task CreateDeploymentAsync_WhenServiceCreationFails_ShouldRollBackStatefulSetAndRethrow()
        {
            var request = new AdapterData
            {
                Name = "adapter1",
                ImageName = "image",
                ImageVersion = "v1",
                ReplicaCount = 1
            };

            _kubeClientMock.Setup(x => x.UpsertServiceAsync(It.IsAny<V1Service>(), "adapter", It.IsAny<CancellationToken>()))
                .ThrowsAsync(HttpError(HttpStatusCode.UnprocessableEntity));

            Func<Task> act = () => _manager.CreateDeploymentAsync(request, ResourceType.Mcp, CancellationToken.None);

            await act.Should().ThrowAsync<HttpOperationException>();
            _kubeClientMock.Verify(x => x.DeleteStatefulSetAsync("adapter1", "created-uid", "adapter", It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task CreateDeploymentAsync_WhenStatefulSetConflictsWithForeignObject_ShouldNotDeleteIt()
        {
            var request = new AdapterData { Name = "adapter1", ImageName = "image", ImageVersion = "v1" };
            _persistedStatefulSet = OwnedStatefulSet("adapter1", "tool");
            _kubeClientMock.Setup(x => x.UpsertStatefulSetAsync(It.IsAny<V1StatefulSet>(), "adapter", It.IsAny<CancellationToken>()))
                .ThrowsAsync(HttpError(HttpStatusCode.Conflict));

            Func<Task> act = () => _manager.CreateDeploymentAsync(request, ResourceType.Mcp, CancellationToken.None);

            await act.Should().ThrowAsync<ArgumentException>();
            _kubeClientMock.Verify(x => x.DeleteStatefulSetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [TestMethod]
        public async Task CreateDeploymentAsync_WhenStatefulSetResponseIsLost_ShouldRemovePersistedStatefulSet()
        {
            var request = new AdapterData { Name = "adapter1", ImageName = "image", ImageVersion = "v1" };
            _kubeClientMock.Setup(x => x.UpsertStatefulSetAsync(It.IsAny<V1StatefulSet>(), "adapter", It.IsAny<CancellationToken>()))
                .Callback<V1StatefulSet, string, CancellationToken>((statefulSet, ns, ct) =>
                {
                    statefulSet.Metadata.Uid = "persisted-uid";
                    _persistedStatefulSet = statefulSet;
                })
                .ThrowsAsync(new TaskCanceledException("timeout"));

            Func<Task> act = () => _manager.CreateDeploymentAsync(request, ResourceType.Mcp, CancellationToken.None);

            await act.Should().ThrowAsync<TaskCanceledException>();
            _kubeClientMock.Verify(x => x.DeleteStatefulSetAsync("adapter1", "persisted-uid", "adapter", It.IsAny<CancellationToken>()), Times.Once);
            _kubeClientMock.Verify(x => x.UpsertServiceAsync(It.IsAny<V1Service>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [TestMethod]
        public async Task CreateDeploymentAsync_WhenServiceResponseIsLost_ShouldRemoveBothPersistedObjects()
        {
            var request = new AdapterData { Name = "adapter1", ImageName = "image", ImageVersion = "v1" };
            _kubeClientMock.Setup(x => x.UpsertServiceAsync(It.IsAny<V1Service>(), "adapter", It.IsAny<CancellationToken>()))
                .Callback<V1Service, string, CancellationToken>((service, ns, ct) =>
                {
                    service.Metadata.Uid = "persisted-service-uid";
                    _persistedService = service;
                })
                .ThrowsAsync(new HttpRequestException("connection reset"));

            Func<Task> act = () => _manager.CreateDeploymentAsync(request, ResourceType.Mcp, CancellationToken.None);

            await act.Should().ThrowAsync<HttpRequestException>();
            _kubeClientMock.Verify(x => x.DeleteServiceAsync("adapter1-service", "persisted-service-uid", "adapter", It.IsAny<CancellationToken>()), Times.Once);
            _kubeClientMock.Verify(x => x.DeleteStatefulSetAsync("adapter1", "created-uid", "adapter", It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task CreateDeploymentAsync_WithToolGatewayName_ShouldThrowWithoutCallingKubernetes()
        {
            var request = new AdapterData
            {
                Name = "toolgateway",
                ImageName = "image",
                ImageVersion = "v1",
                ReplicaCount = 1
            };

            Func<Task> act = () => _manager.CreateDeploymentAsync(request, ResourceType.Mcp, CancellationToken.None);

            await act.Should().ThrowAsync<ArgumentException>().WithMessage("The name is reserved.");
            _kubeClientMock.VerifyNoOtherCalls();
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

            _kubeClientMock.Setup(x => x.ReadStatefulSetAsync("adapter1", "adapter", It.IsAny<CancellationToken>())).ReturnsAsync(OwnedStatefulSet("adapter1"));

            await _manager.UpdateDeploymentAsync(request, ResourceType.Mcp, CancellationToken.None);

            _kubeClientMock.Verify(x => x.PatchStatefulSetAsync(It.IsAny<V1Patch>(), "adapter1", "adapter", It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task UpdateDeploymentAsync_WhenStatefulSetBelongsToOtherResourceType_ShouldThrowWithoutPatching()
        {
            var request = new AdapterData { Name = "shared-name", ImageName = "image", ImageVersion = "v2" };
            _kubeClientMock.Setup(x => x.ReadStatefulSetAsync("shared-name", "adapter", It.IsAny<CancellationToken>())).ReturnsAsync(OwnedStatefulSet("shared-name", "mcp"));

            Func<Task> act = () => _manager.UpdateDeploymentAsync(request, ResourceType.Tool, CancellationToken.None);

            await act.Should().ThrowAsync<UnauthorizedAccessException>();
            _kubeClientMock.Verify(x => x.PatchStatefulSetAsync(It.IsAny<V1Patch>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [TestMethod]
        public async Task UpdateDeploymentAsync_WhenStatefulSetIsNotManagedByGateway_ShouldThrowWithoutPatching()
        {
            var request = new AdapterData { Name = "first-party", ImageName = "image", ImageVersion = "v2" };
            var statefulSet = new V1StatefulSet
            {
                Metadata = new V1ObjectMeta { Name = "first-party", Uid = "sts-uid" },
                Spec = new V1StatefulSetSpec { Selector = new V1LabelSelector { MatchLabels = new Dictionary<string, string> { ["app"] = "first-party" } } }
            };
            _kubeClientMock.Setup(x => x.ReadStatefulSetAsync("first-party", "adapter", It.IsAny<CancellationToken>())).ReturnsAsync(statefulSet);

            Func<Task> act = () => _manager.UpdateDeploymentAsync(request, ResourceType.Mcp, CancellationToken.None);

            await act.Should().ThrowAsync<UnauthorizedAccessException>();
            _kubeClientMock.Verify(x => x.PatchStatefulSetAsync(It.IsAny<V1Patch>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
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

            _kubeClientMock.Setup(x => x.ReadStatefulSetAsync("tool1", "adapter", It.IsAny<CancellationToken>())).ReturnsAsync(OwnedStatefulSet("tool1", "tool"));

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

            _kubeClientMock.Setup(x => x.ReadStatefulSetAsync("adapter1", "adapter", It.IsAny<CancellationToken>())).ReturnsAsync(OwnedStatefulSet("adapter1"));

            V1Patch? capturedPatch = null;
            _kubeClientMock.Setup(x => x.PatchStatefulSetAsync(It.IsAny<V1Patch>(), "adapter1", "adapter", It.IsAny<CancellationToken>()))
                .Callback<V1Patch, string, string, CancellationToken>((patch, name, ns, ct) => capturedPatch = patch)
                .ReturnsAsync(new V1StatefulSet());

            await _manager.UpdateDeploymentAsync(request, ResourceType.Mcp, CancellationToken.None);

            capturedPatch.Should().NotBeNull();
            var content = capturedPatch!.Content as string;
            content.Should().NotBeNull();
            content.Should().Contain("\"adapter/type\":\"mcp\"");
            content.Should().Contain("\"uid\":\"sts-uid\"");
            content.Should().Contain("\"resourceVersion\":\"10\"");
        }

        [TestMethod]
        public async Task DeleteDeploymentAsync_ShouldDeleteResources()
        {
            _kubeClientMock.Setup(x => x.ReadStatefulSetAsync("adapter1", "adapter", It.IsAny<CancellationToken>())).ReturnsAsync(OwnedStatefulSet("adapter1"));
            _kubeClientMock.Setup(x => x.ReadServiceAsync("adapter1-service", "adapter", It.IsAny<CancellationToken>())).ReturnsAsync(OwnedService("adapter1"));

            await _manager.DeleteDeploymentAsync("adapter1", ResourceType.Mcp, CancellationToken.None);

            _kubeClientMock.Verify(x => x.DeleteStatefulSetAsync("adapter1", "sts-uid", "adapter", It.IsAny<CancellationToken>()), Times.Once);
            _kubeClientMock.Verify(x => x.DeleteServiceAsync("adapter1-service", "service-uid", "adapter", It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task DeleteDeploymentAsync_WhenNotFound_ShouldLogWarningAndComplete()
        {
            _kubeClientMock.Setup(x => x.ReadStatefulSetAsync("adapter1", "adapter", It.IsAny<CancellationToken>()))
                .ThrowsAsync(HttpError(HttpStatusCode.NotFound));
            _kubeClientMock.Setup(x => x.ReadServiceAsync("adapter1-service", "adapter", It.IsAny<CancellationToken>()))
                .ThrowsAsync(HttpError(HttpStatusCode.NotFound));

            await _manager.DeleteDeploymentAsync("adapter1", ResourceType.Mcp, CancellationToken.None);

            _kubeClientMock.Verify(x => x.DeleteStatefulSetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
            _kubeClientMock.Verify(x => x.DeleteServiceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [TestMethod]
        public async Task DeleteDeploymentAsync_WhenStatefulSetIsMissing_ShouldStillDeleteOwnedService()
        {
            _kubeClientMock.Setup(x => x.ReadStatefulSetAsync("adapter1", "adapter", It.IsAny<CancellationToken>()))
                .ThrowsAsync(HttpError(HttpStatusCode.NotFound));
            _kubeClientMock.Setup(x => x.ReadServiceAsync("adapter1-service", "adapter", It.IsAny<CancellationToken>())).ReturnsAsync(OwnedService("adapter1"));

            await _manager.DeleteDeploymentAsync("adapter1", ResourceType.Mcp, CancellationToken.None);

            _kubeClientMock.Verify(x => x.DeleteServiceAsync("adapter1-service", "service-uid", "adapter", It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task DeleteDeploymentAsync_WhenResourcesBelongToOtherResourceType_ShouldNotDeleteThem()
        {
            _kubeClientMock.Setup(x => x.ReadStatefulSetAsync("shared-name", "adapter", It.IsAny<CancellationToken>())).ReturnsAsync(OwnedStatefulSet("shared-name", "mcp"));
            _kubeClientMock.Setup(x => x.ReadServiceAsync("shared-name-service", "adapter", It.IsAny<CancellationToken>())).ReturnsAsync(OwnedService("shared-name", "mcp"));

            await _manager.DeleteDeploymentAsync("shared-name", ResourceType.Tool, CancellationToken.None);

            _kubeClientMock.Verify(x => x.DeleteStatefulSetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
            _kubeClientMock.Verify(x => x.DeleteServiceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [TestMethod]
        public async Task DeleteDeploymentAsync_WhenServiceIsNotManagedByGateway_ShouldDeleteOnlyStatefulSet()
        {
            var firstPartyService = new V1Service
            {
                Metadata = new V1ObjectMeta { Name = "mcpgateway-service" },
                Spec = new V1ServiceSpec { Selector = new Dictionary<string, string> { ["app"] = "mcpgateway" } }
            };
            _kubeClientMock.Setup(x => x.ReadStatefulSetAsync("mcpgateway", "adapter", It.IsAny<CancellationToken>())).ReturnsAsync(OwnedStatefulSet("mcpgateway"));
            _kubeClientMock.Setup(x => x.ReadServiceAsync("mcpgateway-service", "adapter", It.IsAny<CancellationToken>())).ReturnsAsync(firstPartyService);

            await _manager.DeleteDeploymentAsync("mcpgateway", ResourceType.Mcp, CancellationToken.None);

            _kubeClientMock.Verify(x => x.DeleteStatefulSetAsync("mcpgateway", "sts-uid", "adapter", It.IsAny<CancellationToken>()), Times.Once);
            _kubeClientMock.Verify(x => x.DeleteServiceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
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
                    Selector = new V1LabelSelector { MatchLabels = OwnerLabels("adapter1", "mcp") },
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

            var result = await _manager.GetDeploymentStatusAsync("adapter1", ResourceType.Mcp, CancellationToken.None);

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
                    Selector = new V1LabelSelector { MatchLabels = OwnerLabels("adapter1", "mcp") },
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

            var result = await _manager.GetDeploymentStatusAsync("adapter1", ResourceType.Mcp, CancellationToken.None);

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
                    Selector = new V1LabelSelector { MatchLabels = OwnerLabels("adapter1", "mcp") },
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

            var result = await _manager.GetDeploymentStatusAsync("adapter1", ResourceType.Mcp, CancellationToken.None);

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
                    Selector = new V1LabelSelector { MatchLabels = OwnerLabels("adapter1", "mcp") },
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

            var result = await _manager.GetDeploymentStatusAsync("adapter1", ResourceType.Mcp, CancellationToken.None);

            result.Image.Should().Be("Unknown");
        }

        [TestMethod]
        public async Task GetDeploymentStatusAsync_WhenStatefulSetBelongsToOtherResourceType_ShouldThrow()
        {
            _kubeClientMock.Setup(x => x.ReadStatefulSetAsync("shared-name", "adapter", It.IsAny<CancellationToken>())).ReturnsAsync(OwnedStatefulSet("shared-name", "mcp"));

            Func<Task> act = () => _manager.GetDeploymentStatusAsync("shared-name", ResourceType.Tool, CancellationToken.None);

            await act.Should().ThrowAsync<UnauthorizedAccessException>();
        }

        [TestMethod]
        public async Task GetDeploymentLogsAsync_ShouldReturnLogText()
        {
            _kubeClientMock.Setup(x => x.ReadStatefulSetAsync("adapter1", "adapter", It.IsAny<CancellationToken>())).ReturnsAsync(OwnedStatefulSet("adapter1"));
            _kubeClientMock.Setup(x => x.ReadPodAsync("adapter1-0", "adapter", It.IsAny<CancellationToken>())).ReturnsAsync(PodOwnedBy("adapter1-0", "adapter1", "sts-uid"));
            _kubeClientMock.Setup(x => x.GetContainerLogStream("adapter1-0", 1000, "adapter", It.IsAny<CancellationToken>())).ReturnsAsync(LogStream("log-content"));

            var result = await _manager.GetDeploymentLogsAsync("adapter1", ResourceType.Mcp, 0, CancellationToken.None);

            result.Should().Be("log-content");
        }

        [TestMethod]
        public async Task GetDeploymentLogsAsync_WithDifferentOrdinal_ShouldUseDifferentPodName()
        {
            _kubeClientMock.Setup(x => x.ReadStatefulSetAsync("adapter1", "adapter", It.IsAny<CancellationToken>())).ReturnsAsync(OwnedStatefulSet("adapter1"));
            _kubeClientMock.Setup(x => x.ReadPodAsync("adapter1-2", "adapter", It.IsAny<CancellationToken>())).ReturnsAsync(PodOwnedBy("adapter1-2", "adapter1", "sts-uid"));
            _kubeClientMock.Setup(x => x.GetContainerLogStream("adapter1-2", 1000, "adapter", It.IsAny<CancellationToken>())).ReturnsAsync(LogStream("logs-from-pod-2"));

            var result = await _manager.GetDeploymentLogsAsync("adapter1", ResourceType.Mcp, 2, CancellationToken.None);

            result.Should().Be("logs-from-pod-2");
            _kubeClientMock.Verify(x => x.GetContainerLogStream("adapter1-2", 1000, "adapter", It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task GetDeploymentLogsAsync_WhenStatefulSetBelongsToOtherResourceType_ShouldThrowWithoutReadingLogs()
        {
            _kubeClientMock.Setup(x => x.ReadStatefulSetAsync("shared-name", "adapter", It.IsAny<CancellationToken>())).ReturnsAsync(OwnedStatefulSet("shared-name", "mcp"));

            Func<Task> act = () => _manager.GetDeploymentLogsAsync("shared-name", ResourceType.Tool, 0, CancellationToken.None);

            await act.Should().ThrowAsync<UnauthorizedAccessException>();
            _kubeClientMock.Verify(x => x.GetContainerLogStream(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [TestMethod]
        public async Task GetDeploymentLogsAsync_WhenPodIsNotOwnedByCurrentStatefulSet_ShouldThrowWithoutReadingLogs()
        {
            _kubeClientMock.Setup(x => x.ReadStatefulSetAsync("adapter1", "adapter", It.IsAny<CancellationToken>())).ReturnsAsync(OwnedStatefulSet("adapter1", uid: "current-uid"));
            _kubeClientMock.Setup(x => x.ReadPodAsync("adapter1-0", "adapter", It.IsAny<CancellationToken>())).ReturnsAsync(PodOwnedBy("adapter1-0", "adapter1", "previous-uid"));

            Func<Task> act = () => _manager.GetDeploymentLogsAsync("adapter1", ResourceType.Mcp, 0, CancellationToken.None);

            await act.Should().ThrowAsync<UnauthorizedAccessException>();
            _kubeClientMock.Verify(x => x.GetContainerLogStream(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
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
