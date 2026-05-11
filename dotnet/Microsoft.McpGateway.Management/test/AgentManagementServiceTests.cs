// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.McpGateway.Management.Authorization;
using Microsoft.McpGateway.Management.Contracts;
using Microsoft.McpGateway.Management.Service;
using Microsoft.McpGateway.Management.Store;
using ModelContextProtocol.Protocol;
using Moq;

namespace Microsoft.McpGateway.Management.Tests
{
    [TestClass]
    public class AgentManagementServiceTests
    {
        private readonly Mock<IAgentResourceStore> _agentStoreMock;
        private readonly Mock<IToolResourceStore> _toolStoreMock;
        private readonly Mock<IPermissionProvider> _permissionProviderMock;
        private readonly Mock<ILogger<AgentManagementService>> _loggerMock;
        private readonly AgentManagementService _service;
        private readonly ClaimsPrincipal _accessContext;

        public AgentManagementServiceTests()
        {
            _agentStoreMock = new Mock<IAgentResourceStore>();
            _toolStoreMock = new Mock<IToolResourceStore>();
            _permissionProviderMock = new Mock<IPermissionProvider>();
            _loggerMock = new Mock<ILogger<AgentManagementService>>();

            _permissionProviderMock
                .Setup(x => x.CheckAccessAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<IManagedResource>(), It.IsAny<Operation>()))
                .ReturnsAsync(true);
            _permissionProviderMock
                .Setup(x => x.CheckAccessAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<IEnumerable<AgentResource>>(), Operation.Read))
                .ReturnsAsync((ClaimsPrincipal _, IEnumerable<AgentResource> r, Operation _) => r.ToArray());

            _service = new AgentManagementService(
                _agentStoreMock.Object,
                _toolStoreMock.Object,
                _permissionProviderMock.Object,
                _loggerMock.Object);

            _accessContext = new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, "user1")]));
        }

        private static AgentData CreateAgentData(
            string name = "demo-agent",
            IList<string>? tools = null) =>
            new()
            {
                Name = name,
                Model = "gpt-4o",
                System = "you are helpful.",
                Tools = tools ?? new List<string>(),
                Description = "test",
            };

        private static ToolResource CreateToolResource(string name)
        {
            var schemaJson = System.Text.Json.JsonSerializer.Serialize(new { type = "object", properties = new { } });
            var schema = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(schemaJson);
            var data = new ToolData
            {
                Name = name,
                ImageName = "img",
                ImageVersion = "v1",
                ReplicaCount = 1,
                EnvironmentVariables = [],
                ToolDefinition = new ToolDefinition
                {
                    Tool = new Tool { Name = name, Description = "t", InputSchema = schema },
                },
            };
            return ToolResource.Create(data, "user1", DateTimeOffset.UtcNow);
        }

        [TestMethod]
        public async Task CreateAsync_ShouldSucceed_WithEmptyTools()
        {
            var request = CreateAgentData("agent-a");
            _agentStoreMock.Setup(x => x.TryGetAsync("agent-a", It.IsAny<CancellationToken>())).ReturnsAsync((AgentResource?)null);

            var result = await _service.CreateAsync(_accessContext, request, CancellationToken.None);

            result.Name.Should().Be("agent-a");
            _agentStoreMock.Verify(x => x.UpsertAsync(It.IsAny<AgentResource>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task CreateAsync_ShouldThrow_WhenNameInvalid()
        {
            var request = CreateAgentData("Bad_Name");

            Func<Task> act = () => _service.CreateAsync(_accessContext, request, CancellationToken.None);

            await act.Should().ThrowAsync<ArgumentException>()
                .WithMessage("Name must contain only lowercase letters, numbers, and dashes.");
        }

        [TestMethod]
        public async Task CreateAsync_ShouldThrow_WhenAgentAlreadyExists()
        {
            var request = CreateAgentData("dup");
            var existing = AgentResource.Create(request, "user1", DateTimeOffset.UtcNow);
            _agentStoreMock.Setup(x => x.TryGetAsync("dup", It.IsAny<CancellationToken>())).ReturnsAsync(existing);

            Func<Task> act = () => _service.CreateAsync(_accessContext, request, CancellationToken.None);

            await act.Should().ThrowAsync<ArgumentException>()
                .WithMessage("An agent with the same name already exists.");
        }

        [TestMethod]
        public async Task CreateAsync_ShouldAcceptKnownBuiltins()
        {
            var request = CreateAgentData("agent-b", new List<string>
            {
                "builtin:bash", "builtin:read_file", "builtin:write_file",
            });
            _agentStoreMock.Setup(x => x.TryGetAsync("agent-b", It.IsAny<CancellationToken>())).ReturnsAsync((AgentResource?)null);

            var result = await _service.CreateAsync(_accessContext, request, CancellationToken.None);

            result.Tools.Should().BeEquivalentTo(request.Tools);
        }

        [TestMethod]
        public async Task CreateAsync_ShouldThrow_OnUnknownBuiltin()
        {
            var request = CreateAgentData("agent-c", new List<string> { "builtin:nope" });
            _agentStoreMock.Setup(x => x.TryGetAsync("agent-c", It.IsAny<CancellationToken>())).ReturnsAsync((AgentResource?)null);

            Func<Task> act = () => _service.CreateAsync(_accessContext, request, CancellationToken.None);

            await act.Should().ThrowAsync<ArgumentException>().WithMessage("*builtin:nope*");
        }

        [TestMethod]
        public async Task CreateAsync_ShouldThrow_OnUnprefixedTool()
        {
            var request = CreateAgentData("agent-d", new List<string> { "weather" });
            _agentStoreMock.Setup(x => x.TryGetAsync("agent-d", It.IsAny<CancellationToken>())).ReturnsAsync((AgentResource?)null);

            Func<Task> act = () => _service.CreateAsync(_accessContext, request, CancellationToken.None);

            await act.Should().ThrowAsync<ArgumentException>().WithMessage("*missing a recognized prefix*");
        }

        [TestMethod]
        public async Task CreateAsync_ShouldThrow_WhenMcpToolMissing()
        {
            var request = CreateAgentData("agent-e", new List<string> { "mcp:weather" });
            _agentStoreMock.Setup(x => x.TryGetAsync("agent-e", It.IsAny<CancellationToken>())).ReturnsAsync((AgentResource?)null);
            _toolStoreMock.Setup(x => x.TryGetAsync("weather", It.IsAny<CancellationToken>())).ReturnsAsync((ToolResource?)null);

            Func<Task> act = () => _service.CreateAsync(_accessContext, request, CancellationToken.None);

            await act.Should().ThrowAsync<ArgumentException>().WithMessage("*MCP tool 'weather'*does not exist*");
        }

        [TestMethod]
        public async Task CreateAsync_ShouldThrow_WhenCallerLacksMcpToolAccess()
        {
            var request = CreateAgentData("agent-f", new List<string> { "mcp:weather" });
            _agentStoreMock.Setup(x => x.TryGetAsync("agent-f", It.IsAny<CancellationToken>())).ReturnsAsync((AgentResource?)null);
            var tool = CreateToolResource("weather");
            _toolStoreMock.Setup(x => x.TryGetAsync("weather", It.IsAny<CancellationToken>())).ReturnsAsync(tool);
            _permissionProviderMock
                .Setup(x => x.CheckAccessAsync(It.IsAny<ClaimsPrincipal>(), It.Is<IManagedResource>(r => r is ToolResource), Operation.Read))
                .ReturnsAsync(false);

            Func<Task> act = () => _service.CreateAsync(_accessContext, request, CancellationToken.None);

            await act.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*permission to reference tool 'weather'*");
        }

        [TestMethod]
        public async Task CreateAsync_ShouldThrow_WhenSubAgentMissing()
        {
            var request = CreateAgentData("agent-g", new List<string> { "agent:peer" });
            _agentStoreMock.Setup(x => x.TryGetAsync("agent-g", It.IsAny<CancellationToken>())).ReturnsAsync((AgentResource?)null);
            _agentStoreMock.Setup(x => x.TryGetAsync("peer", It.IsAny<CancellationToken>())).ReturnsAsync((AgentResource?)null);

            Func<Task> act = () => _service.CreateAsync(_accessContext, request, CancellationToken.None);

            await act.Should().ThrowAsync<ArgumentException>().WithMessage("*Peer agent 'peer'*");
        }

        [TestMethod]
        public async Task CreateAsync_ShouldThrow_WhenAgentSelfReferences()
        {
            var request = CreateAgentData("agent-h", new List<string> { "agent:agent-h" });
            _agentStoreMock.Setup(x => x.TryGetAsync("agent-h", It.IsAny<CancellationToken>())).ReturnsAsync((AgentResource?)null);

            Func<Task> act = () => _service.CreateAsync(_accessContext, request, CancellationToken.None);

            await act.Should().ThrowAsync<ArgumentException>().WithMessage("*cannot reference itself*");
        }

        [TestMethod]
        public async Task UpdateAsync_ShouldRevalidateTools()
        {
            var existing = AgentResource.Create(CreateAgentData("agent-i"), "user1", DateTimeOffset.UtcNow);
            _agentStoreMock.Setup(x => x.TryGetAsync("agent-i", It.IsAny<CancellationToken>())).ReturnsAsync(existing);

            var update = CreateAgentData("agent-i", new List<string> { "mcp:missing" });
            _toolStoreMock.Setup(x => x.TryGetAsync("missing", It.IsAny<CancellationToken>())).ReturnsAsync((ToolResource?)null);

            Func<Task> act = () => _service.UpdateAsync(_accessContext, update, CancellationToken.None);

            await act.Should().ThrowAsync<ArgumentException>().WithMessage("*MCP tool 'missing'*");
        }

        [TestMethod]
        public async Task GetAsync_ShouldReturnNull_WhenAgentMissing()
        {
            _agentStoreMock.Setup(x => x.TryGetAsync("nope", It.IsAny<CancellationToken>())).ReturnsAsync((AgentResource?)null);

            var result = await _service.GetAsync(_accessContext, "nope", CancellationToken.None);

            result.Should().BeNull();
        }

        [TestMethod]
        public async Task GetAsync_ShouldThrow_WhenCallerLacksReadAccess()
        {
            var existing = AgentResource.Create(CreateAgentData("agent-j"), "user1", DateTimeOffset.UtcNow);
            _agentStoreMock.Setup(x => x.TryGetAsync("agent-j", It.IsAny<CancellationToken>())).ReturnsAsync(existing);
            _permissionProviderMock
                .Setup(x => x.CheckAccessAsync(It.IsAny<ClaimsPrincipal>(), It.Is<IManagedResource>(r => r is AgentResource), Operation.Read))
                .ReturnsAsync(false);

            Func<Task> act = () => _service.GetAsync(_accessContext, "agent-j", CancellationToken.None);

            await act.Should().ThrowAsync<UnauthorizedAccessException>();
        }

        [TestMethod]
        public async Task ListAsync_ShouldFilterByPermission()
        {
            var a1 = AgentResource.Create(CreateAgentData("a1"), "user1", DateTimeOffset.UtcNow);
            var a2 = AgentResource.Create(CreateAgentData("a2"), "user2", DateTimeOffset.UtcNow);
            _agentStoreMock.Setup(x => x.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[] { a1, a2 });
            _permissionProviderMock
                .Setup(x => x.CheckAccessAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<IEnumerable<AgentResource>>(), Operation.Read))
                .ReturnsAsync((ClaimsPrincipal _, IEnumerable<AgentResource> rs, Operation _) => rs.Where(r => r.Name == "a1").ToArray());

            var result = (await _service.ListAsync(_accessContext, CancellationToken.None)).ToList();

            result.Should().HaveCount(1);
            result[0].Name.Should().Be("a1");
        }

        [TestMethod]
        public async Task DeleteAsync_ShouldRequireWrite()
        {
            var existing = AgentResource.Create(CreateAgentData("agent-k"), "user1", DateTimeOffset.UtcNow);
            _agentStoreMock.Setup(x => x.TryGetAsync("agent-k", It.IsAny<CancellationToken>())).ReturnsAsync(existing);
            _permissionProviderMock
                .Setup(x => x.CheckAccessAsync(It.IsAny<ClaimsPrincipal>(), It.Is<IManagedResource>(r => r is AgentResource), Operation.Write))
                .ReturnsAsync(false);

            Func<Task> act = () => _service.DeleteAsync(_accessContext, "agent-k", CancellationToken.None);

            await act.Should().ThrowAsync<UnauthorizedAccessException>();
            _agentStoreMock.Verify(x => x.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }
    }
}
