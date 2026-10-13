// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.McpGateway.Management.Authorization;
using Microsoft.McpGateway.Management.Contracts;
using Microsoft.McpGateway.Management.Deployment;
using Microsoft.McpGateway.Management.Service;
using Microsoft.McpGateway.Management.Store;
using ModelContextProtocol.Protocol;
using Moq;

namespace Microsoft.McpGateway.Management.Tests
{
    // Real services, stores, permission provider and deployment manager over a fake cluster namespace shared by adapters and tools.
    [TestClass]
    public class ResourceNameCollisionTests
    {
        private const string SharedName = "shared-name";
        private readonly FakeKubeClient _cluster = new();
        private readonly InMemoryAdapterResourceStore _adapterStore = new();
        private readonly InMemoryToolResourceStore _toolStore = new();
        private readonly KubernetesAdapterDeploymentManager _deploymentManager;
        private readonly AdapterManagementService _adapters;
        private readonly ToolManagementService _tools;
        private readonly AdapterRichResultProvider _richResults;
        private readonly ClaimsPrincipal _owner = User("owner");
        private readonly ClaimsPrincipal _otherUser = User("other-user");

        public ResourceNameCollisionTests()
        {
            var permissions = new SimplePermissionProvider();
            var workloadIdentity = Mock.Of<IWorkloadIdentityAuthorizer>();
            _deploymentManager = new KubernetesAdapterDeploymentManager("registry.io", _cluster, NullLogger<KubernetesAdapterDeploymentManager>.Instance);
            _adapters = new AdapterManagementService(_deploymentManager, _adapterStore, _toolStore, permissions, workloadIdentity, NullLogger<AdapterManagementService>.Instance);
            _tools = new ToolManagementService(_deploymentManager, _toolStore, _adapterStore, permissions, workloadIdentity, NullLogger<ToolManagementService>.Instance);
            _richResults = new AdapterRichResultProvider(_deploymentManager, NullLogger<AdapterManagementService>.Instance);
        }

        [TestMethod]
        public async Task CreateTool_WithNameOfAnotherUsersAdapter_IsRejected()
        {
            await _adapters.CreateAsync(_owner, AdapterRequest(SharedName), CancellationToken.None);
            _cluster.PodLogs[$"{SharedName}-0"] = "owner-log";

            Func<Task> act = () => _tools.CreateAsync(_otherUser, ToolRequest(SharedName), CancellationToken.None);

            await act.Should().ThrowAsync<ArgumentException>();
            (await _toolStore.TryGetAsync(SharedName, CancellationToken.None)).Should().BeNull();
            _cluster.StatefulSets.Should().ContainKey(SharedName);
            _cluster.Services.Should().ContainKey($"{SharedName}-service");
            (await _richResults.GetAdapterLogsAsync(SharedName, ResourceType.Mcp, 0, CancellationToken.None)).Should().Be("owner-log");
        }

        [TestMethod]
        public async Task CreateAdapter_WithNameOfAnotherUsersTool_IsRejected()
        {
            await _tools.CreateAsync(_owner, ToolRequest(SharedName), CancellationToken.None);

            Func<Task> act = () => _adapters.CreateAsync(_otherUser, AdapterRequest(SharedName), CancellationToken.None);

            await act.Should().ThrowAsync<ArgumentException>();
            (await _adapterStore.TryGetAsync(SharedName, CancellationToken.None)).Should().BeNull();
            _cluster.StatefulSets[SharedName].Spec.Selector.MatchLabels["adapter/type"].Should().Be("tool");
            _cluster.Services.Should().ContainKey($"{SharedName}-service");
        }

        [TestMethod]
        public async Task Create_WhenWorkloadExistsWithoutRecord_DoesNotAdoptIt()
        {
            // Another create has deployed the workload but not yet stored its record.
            await _deploymentManager.CreateDeploymentAsync(AdapterRequest(SharedName), ResourceType.Mcp, CancellationToken.None);

            Func<Task> act = () => _tools.CreateAsync(_otherUser, ToolRequest(SharedName), CancellationToken.None);

            await act.Should().ThrowAsync<ArgumentException>().WithMessage("A Kubernetes resource with the same name already exists.");
            (await _toolStore.TryGetAsync(SharedName, CancellationToken.None)).Should().BeNull();
            _cluster.StatefulSets[SharedName].Spec.Selector.MatchLabels["adapter/type"].Should().Be("mcp");
            _cluster.Services.Should().ContainKey($"{SharedName}-service");
        }

        [TestMethod]
        public async Task ExistingCollidingToolRecord_CannotReachAdapterWorkload()
        {
            await _adapters.CreateAsync(_owner, AdapterRequest(SharedName), CancellationToken.None);
            _cluster.PodLogs[$"{SharedName}-0"] = "owner-log";
            // A same-named record created before names were unique across resource types.
            await _toolStore.UpsertAsync(ToolResource.Create(ToolRequest(SharedName), "other-user", DateTimeOffset.UtcNow), CancellationToken.None);
            var tool = await _tools.GetAsync(_otherUser, SharedName, CancellationToken.None);
            var update = ToolRequest(SharedName);
            update.ImageVersion = "v2";

            Func<Task> readLogs = () => _richResults.GetAdapterLogsAsync(tool!.Name, ResourceType.Tool, 0, CancellationToken.None);
            Func<Task> readStatus = () => _richResults.GetAdapterStatusAsync(tool!.Name, ResourceType.Tool, CancellationToken.None);
            Func<Task> patch = () => _tools.UpdateAsync(_otherUser, update, CancellationToken.None);

            await readLogs.Should().ThrowAsync<UnauthorizedAccessException>();
            await readStatus.Should().ThrowAsync<UnauthorizedAccessException>();
            await patch.Should().ThrowAsync<UnauthorizedAccessException>();

            await _tools.DeleteAsync(_otherUser, SharedName, CancellationToken.None);

            (await _toolStore.TryGetAsync(SharedName, CancellationToken.None)).Should().BeNull();
            (await _adapterStore.TryGetAsync(SharedName, CancellationToken.None)).Should().NotBeNull();
            _cluster.StatefulSets.Should().ContainKey(SharedName);
            _cluster.Services.Should().ContainKey($"{SharedName}-service");
            _cluster.PatchedStatefulSets.Should().BeEmpty();
            (await _richResults.GetAdapterLogsAsync(SharedName, ResourceType.Mcp, 0, CancellationToken.None)).Should().Be("owner-log");
        }

        private static ClaimsPrincipal User(string userId) =>
            new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "test"));

        [DataTestMethod]
        [DataRow(ResourceType.Mcp, ResourceType.Tool)]
        [DataRow(ResourceType.Tool, ResourceType.Mcp)]
        public async Task Routing_RejectsAWorkloadOfTheOtherType(ResourceType deployedType, ResourceType requestedType)
        {
            await _deploymentManager.CreateDeploymentAsync(AdapterRequest(SharedName), deployedType, CancellationToken.None);

            Func<Task> act = () => DeploymentOwnership.VerifyServiceAsync(_cluster, SharedName, requestedType, "adapter", CancellationToken.None);

            await act.Should().ThrowAsync<UnauthorizedAccessException>();
        }

        [DataTestMethod]
        [DataRow(ResourceType.Mcp)]
        [DataRow(ResourceType.Tool)]
        public async Task Routing_RejectsAMismatchedServiceEvenWhenTheWorkloadMatches(ResourceType resourceType)
        {
            await _deploymentManager.CreateDeploymentAsync(AdapterRequest(SharedName), resourceType, CancellationToken.None);
            _cluster.Services[$"{SharedName}-service"].Spec.Selector = new Dictionary<string, string> { ["app"] = "first-party" };

            Func<Task> act = () => DeploymentOwnership.VerifyServiceAsync(_cluster, SharedName, resourceType, "adapter", CancellationToken.None);

            await act.Should().ThrowAsync<UnauthorizedAccessException>();
        }

        [DataTestMethod]
        [DataRow(ResourceType.Mcp)]
        [DataRow(ResourceType.Tool)]
        public async Task Routing_RejectsAMissingWorkload(ResourceType resourceType)
        {
            Func<Task> act = () => DeploymentOwnership.VerifyServiceAsync(_cluster, SharedName, resourceType, "adapter", CancellationToken.None);

            await act.Should().ThrowAsync<UnauthorizedAccessException>();
        }

        [DataTestMethod]
        [DataRow(ResourceType.Mcp)]
        [DataRow(ResourceType.Tool)]
        public async Task Routing_AllowsMatchingWorkloadAndService(ResourceType resourceType)
        {
            await _deploymentManager.CreateDeploymentAsync(AdapterRequest(SharedName), resourceType, CancellationToken.None);

            await DeploymentOwnership.VerifyServiceAsync(_cluster, SharedName, resourceType, "adapter", CancellationToken.None);
        }

        private static AdapterData AdapterRequest(string name) =>
            new() { Name = name, ImageName = "example-adapter", ImageVersion = "v1" };

        private static ToolData ToolRequest(string name) => new()
        {
            Name = name,
            ImageName = "example-tool",
            ImageVersion = "v1",
            ToolDefinition = new ToolDefinition
            {
                Tool = new Tool
                {
                    Name = name,
                    Description = "Example tool",
                    InputSchema = JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{}}""")
                },
                Port = 8000,
                Path = "/mcp"
            }
        };

        // Creating an existing name conflicts and reading a missing object returns 404, as in Kubernetes.
        private sealed class FakeKubeClient : IKubeClientWrapper
        {
            public Dictionary<string, V1StatefulSet> StatefulSets { get; } = [];
            public Dictionary<string, V1Service> Services { get; } = [];
            public Dictionary<string, V1Pod> Pods { get; } = [];
            public Dictionary<string, string> PodLogs { get; } = [];
            public List<string> PatchedStatefulSets { get; } = [];

            public Task<V1StatefulSet> UpsertStatefulSetAsync(V1StatefulSet statefulSet, string? ns = null, CancellationToken cancellationToken = default)
            {
                var name = statefulSet.Metadata.Name;
                if (StatefulSets.ContainsKey(name))
                    throw HttpError(HttpStatusCode.Conflict);

                statefulSet.Metadata.Uid = Guid.NewGuid().ToString();
                statefulSet.Metadata.ResourceVersion = "1";
                StatefulSets[name] = statefulSet;
                for (var ordinal = 0; ordinal < (statefulSet.Spec.Replicas ?? 1); ordinal++)
                {
                    var podName = $"{name}-{ordinal}";
                    Pods[podName] = new V1Pod
                    {
                        Metadata = new V1ObjectMeta
                        {
                            Name = podName,
                            Labels = statefulSet.Spec.Template.Metadata.Labels,
                            OwnerReferences = [new V1OwnerReference { Kind = "StatefulSet", Name = name, Uid = statefulSet.Metadata.Uid, Controller = true }]
                        }
                    };
                }

                return Task.FromResult(statefulSet);
            }

            public Task<V1Service> UpsertServiceAsync(V1Service service, string? ns = null, CancellationToken ct = default)
            {
                if (Services.ContainsKey(service.Metadata.Name))
                    throw HttpError(HttpStatusCode.Conflict);

                service.Metadata.Uid = Guid.NewGuid().ToString();
                Services[service.Metadata.Name] = service;
                return Task.FromResult(service);
            }

            public Task<V1StatefulSet> ReadStatefulSetAsync(string name, string? ns = null, CancellationToken cancellationToken = default) =>
                Task.FromResult(StatefulSets.TryGetValue(name, out var statefulSet) ? statefulSet : throw HttpError(HttpStatusCode.NotFound));

            public Task<V1Service> ReadServiceAsync(string name, string? ns = null, CancellationToken cancellationToken = default) =>
                Task.FromResult(Services.TryGetValue(name, out var service) ? service : throw HttpError(HttpStatusCode.NotFound));

            public Task<V1Pod> ReadPodAsync(string name, string? ns = null, CancellationToken cancellationToken = default) =>
                Task.FromResult(Pods.TryGetValue(name, out var pod) ? pod : throw HttpError(HttpStatusCode.NotFound));

            public Task<Stream> GetContainerLogStream(string name, int tailLines = 1000, string? ns = null, CancellationToken cancellationToken = default)
            {
                if (!Pods.ContainsKey(name))
                    throw HttpError(HttpStatusCode.NotFound);

                return Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(PodLogs.GetValueOrDefault(name, string.Empty))));
            }

            public Task<V1StatefulSet> PatchStatefulSetAsync(V1Patch patch, string statefulSetName, string? ns = null, CancellationToken ct = default)
            {
                var statefulSet = StatefulSets.TryGetValue(statefulSetName, out var existing) ? existing : throw HttpError(HttpStatusCode.NotFound);
                PatchedStatefulSets.Add(statefulSetName);
                return Task.FromResult(statefulSet);
            }

            public Task<V1Status> DeleteStatefulSetAsync(string name, string uid, string? ns = null, CancellationToken cancellationToken = default)
            {
                if (StatefulSets.TryGetValue(name, out var current) && current.Metadata.Uid != uid)
                    throw HttpError(HttpStatusCode.Conflict);
                if (!StatefulSets.Remove(name, out var statefulSet))
                    throw HttpError(HttpStatusCode.NotFound);

                foreach (var podName in Pods.Where(p => p.Value.Metadata.OwnerReferences.Any(o => o.Uid == statefulSet.Metadata.Uid)).Select(p => p.Key).ToList())
                    Pods.Remove(podName);

                return Task.FromResult(new V1Status());
            }

            public Task<V1Service> DeleteServiceAsync(string name, string uid, string? ns = null, CancellationToken cancellationToken = default)
            {
                if (Services.TryGetValue(name, out var current) && current.Metadata.Uid != uid)
                    throw HttpError(HttpStatusCode.Conflict);
                return Task.FromResult(Services.Remove(name, out var service) ? service : throw HttpError(HttpStatusCode.NotFound));
            }

            public Task<V1StatefulSetList> ListStatefulSetsAsync(string? ns = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

            public Task<V1Deployment> ReadDeploymentAsync(string name, string? ns = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

            public Task<V1StatefulSet> ReplaceStatefulSetAsync(V1StatefulSet statefulSet, string statefulSetName, string? ns = null, CancellationToken ct = default) => throw new NotSupportedException();

            private static HttpOperationException HttpError(HttpStatusCode statusCode) => new()
            {
                Response = new HttpResponseMessageWrapper(new HttpResponseMessage(statusCode), statusCode.ToString())
            };
        }
    }
}
