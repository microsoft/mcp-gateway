// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.McpGateway.Management.Authorization;
using Microsoft.McpGateway.Management.Contracts;
using Microsoft.McpGateway.Management.Store;
using Microsoft.McpGateway.Tools.Services;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Moq;

namespace Microsoft.McpGateway.Tools.Tests
{
    [TestClass]
    public class StorageToolDefinitionProviderTests
    {
        private readonly Mock<IToolResourceStore> _toolResourceStoreMock;
        private readonly Mock<IPermissionProvider> _permissionProviderMock;
        private readonly Mock<IHttpContextAccessor> _httpContextAccessorMock;
        private readonly Mock<ILogger<StorageToolDefinitionProvider>> _loggerMock;
        private readonly StorageToolDefinitionProvider _provider;
        private HttpContext? _currentHttpContext;

        public StorageToolDefinitionProviderTests()
        {
            _toolResourceStoreMock = new Mock<IToolResourceStore>();
            _permissionProviderMock = new Mock<IPermissionProvider>();
            _httpContextAccessorMock = new Mock<IHttpContextAccessor>();
            _loggerMock = new Mock<ILogger<StorageToolDefinitionProvider>>();
            _currentHttpContext = new DefaultHttpContext
            {
                User = CreatePrincipal()
            };

            _httpContextAccessorMock.Setup(x => x.HttpContext).Returns(() => _currentHttpContext);
            SetupPermissionProviderToAllowAll();

            _provider = new StorageToolDefinitionProvider(
                _toolResourceStoreMock.Object,
                _permissionProviderMock.Object,
                _httpContextAccessorMock.Object,
                _loggerMock.Object);
        }

        private static ClaimsPrincipal CreatePrincipal(string userName = "test-user")
        {
            var identity = new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.Name, userName) },
                "TestAuth");

            return new ClaimsPrincipal(identity);
        }

        private void SetupPermissionProviderToAllowAll()
        {
            _permissionProviderMock
                .Setup(x => x.CheckAccessAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<IEnumerable<ToolResource>>(), Operation.Read))
                .ReturnsAsync((ClaimsPrincipal _, IEnumerable<ToolResource> resources, Operation _) => resources.ToArray());

            _permissionProviderMock
                .Setup(x => x.CheckAccessAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<ToolResource>(), Operation.Read))
                .ReturnsAsync(true);
        }

        private static ToolResource CreateToolResource(
            string name,
            string description = "Test tool",
            int port = 443,
            string path = "/score")
        {
            var inputSchemaJson = JsonSerializer.Serialize(new { type = "object", properties = new { } });
            var inputSchema = JsonSerializer.Deserialize<JsonElement>(inputSchemaJson);

            var toolData = new ToolData
            {
                Name = name,
                ImageName = $"{name}-image",
                ImageVersion = "v1",
                ToolDefinition = new ToolDefinition
                {
                    Tool = new Tool
                    {
                        Name = name,
                        Description = description,
                        InputSchema = inputSchema
                    },
                    Port = port,
                    Path = path
                }
            };

            return ToolResource.Create(toolData, "user1", DateTimeOffset.UtcNow);
        }

        private static RequestContext<ListToolsRequestParams> CreateListToolsContext()
        {
            var mcpServerMock = new Mock<McpServer>();
            var jsonRpcRequest = new JsonRpcRequest
            {
                Id = new RequestId(1),
                Method = "tools/list"
            };

            return new RequestContext<ListToolsRequestParams>(mcpServerMock.Object, jsonRpcRequest)
            {
                Params = new ListToolsRequestParams()
            };
        }

        [TestMethod]
        public async Task GetToolDefinitionsAsync_ShouldReturnToolsFromStore()
        {
            // Arrange
            var tools = new List<ToolResource>
            {
                CreateToolResource("tool1", "First tool"),
                CreateToolResource("tool2", "Second tool"),
                CreateToolResource("tool3", "Third tool")
            };

            _toolResourceStoreMock.Setup(x => x.ListAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(tools);

            // Act
            var result = await _provider.GetToolDefinitionsAsync(CancellationToken.None);

            // Assert
            result.Should().HaveCount(3);
            result.Should().Contain(td => td.Tool.Name == "tool1" && td.Tool.Description == "First tool");
            result.Should().Contain(td => td.Tool.Name == "tool2" && td.Tool.Description == "Second tool");
            result.Should().Contain(td => td.Tool.Name == "tool3" && td.Tool.Description == "Third tool");
        }

        [TestMethod]
        public async Task GetToolDefinitionsAsync_ShouldReturnEmptyList_WhenNoToolsExist()
        {
            // Arrange
            _toolResourceStoreMock.Setup(x => x.ListAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<ToolResource>());

            _currentHttpContext = null;

            // Act
            var result = await _provider.GetToolDefinitionsAsync(CancellationToken.None);

            // Assert
            result.Should().BeEmpty();
        }

        [TestMethod]
        public async Task GetToolDefinitionsAsync_ShouldFilterOutUnauthorizedTools()
        {
            // Arrange
            var authorizedTool = CreateToolResource("authorized");
            var unauthorizedTool = CreateToolResource("unauthorized");
            var tools = new List<ToolResource> { authorizedTool, unauthorizedTool };

            _toolResourceStoreMock.Setup(x => x.ListAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(tools);

            _permissionProviderMock
                .Setup(x => x.CheckAccessAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<IEnumerable<ToolResource>>(), Operation.Read))
                .ReturnsAsync((ClaimsPrincipal _, IEnumerable<ToolResource> resources, Operation _) =>
                    resources.Where(r => r.ToolDefinition?.Tool.Name == "authorized").ToArray());

            // Act
            var result = await _provider.GetToolDefinitionsAsync(CancellationToken.None);

            // Assert
            result.Should().ContainSingle(td => td.Tool.Name == "authorized");
        }

        [TestMethod]
        public async Task GetToolDefinitionsAsync_ShouldCacheResults_ForSubsequentCalls()
        {
            // Arrange
            var tools = new List<ToolResource>
            {
                CreateToolResource("tool1")
            };

            _toolResourceStoreMock.Setup(x => x.ListAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(tools);

            // Act
            var result1 = await _provider.GetToolDefinitionsAsync(CancellationToken.None);
            var result2 = await _provider.GetToolDefinitionsAsync(CancellationToken.None);

            // Assert
            result1.Should().BeEquivalentTo(result2);
            _toolResourceStoreMock.Verify(x => x.ListAsync(It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task GetToolDefinitionsAsync_ShouldReturnEmptyList_WhenExceptionOccurs()
        {
            // Arrange
            _toolResourceStoreMock.Setup(x => x.ListAsync(It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("Store error"));

            // Act
            var result = await _provider.GetToolDefinitionsAsync(CancellationToken.None);

            // Assert
            result.Should().BeEmpty();
        }

        [TestMethod]
        public async Task GetToolDefinitionsAsync_ShouldLogError_WhenExceptionOccurs()
        {
            // Arrange
            _toolResourceStoreMock.Setup(x => x.ListAsync(It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("Store error"));

            // Act
            await _provider.GetToolDefinitionsAsync(CancellationToken.None);

            // Assert
            _loggerMock.Verify(
                x => x.Log(
                    LogLevel.Error,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => true),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);
        }

        [TestMethod]
        public async Task GetToolDefinitionAsync_ShouldReturnToolDefinition_WhenToolExists()
        {
            // Arrange
            var toolName = "test-tool";
            var toolResource = CreateToolResource(toolName, "Test tool description", 8000, "/execute");

            _toolResourceStoreMock.Setup(x => x.TryGetAsync(toolName, It.IsAny<CancellationToken>()))
                .ReturnsAsync(toolResource);

            // Act
            var result = await _provider.GetToolDefinitionAsync(toolName, CancellationToken.None);

            // Assert
            result.Should().NotBeNull();
            result!.Tool.Name.Should().Be(toolName);
            result.Tool.Description.Should().Be("Test tool description");
            result.Port.Should().Be(8000);
            result.Path.Should().Be("/execute");
        }

        [TestMethod]
        public async Task GetToolDefinitionAsync_ShouldReturnNull_WhenToolDoesNotExist()
        {
            // Arrange
            _toolResourceStoreMock.Setup(x => x.TryGetAsync("nonexistent", It.IsAny<CancellationToken>()))
                .ReturnsAsync((ToolResource?)null);

            // Act
            var result = await _provider.GetToolDefinitionAsync("nonexistent", CancellationToken.None);

            // Assert
            result.Should().BeNull();
        }

        [TestMethod]
        public async Task GetToolDefinitionAsync_ShouldReturnNull_WhenPermissionDenied()
        {
            // Arrange
            var toolName = "restricted";
            var toolResource = CreateToolResource(toolName);

            _toolResourceStoreMock.Setup(x => x.TryGetAsync(toolName, It.IsAny<CancellationToken>()))
                .ReturnsAsync(toolResource);

            _permissionProviderMock
                .Setup(x => x.CheckAccessAsync(It.IsAny<ClaimsPrincipal>(), toolResource, Operation.Read))
                .ReturnsAsync(false);

            // Act
            var result = await _provider.GetToolDefinitionAsync(toolName, CancellationToken.None);

            // Assert
            result.Should().BeNull();
        }

        [TestMethod]
        public async Task GetToolDefinitionAsync_ShouldReturnNull_WhenUserContextMissing()
        {
            // Arrange
            var toolName = "test-tool";
            var toolResource = CreateToolResource(toolName);

            _toolResourceStoreMock.Setup(x => x.TryGetAsync(toolName, It.IsAny<CancellationToken>()))
                .ReturnsAsync(toolResource);

            _currentHttpContext = null;

            // Act
            var result = await _provider.GetToolDefinitionAsync(toolName, CancellationToken.None);

            // Assert
            result.Should().BeNull();
        }

        [TestMethod]
        public async Task GetToolDefinitionAsync_ShouldReturnNull_WhenExceptionOccurs()
        {
            // Arrange
            _toolResourceStoreMock.Setup(x => x.TryGetAsync("tool1", It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("Store error"));

            // Act
            var result = await _provider.GetToolDefinitionAsync("tool1", CancellationToken.None);

            // Assert
            result.Should().BeNull();
        }

        [TestMethod]
        public async Task GetToolDefinitionAsync_ShouldLogError_WhenExceptionOccurs()
        {
            // Arrange
            _toolResourceStoreMock.Setup(x => x.TryGetAsync("tool1", It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("Store error"));

            // Act
            await _provider.GetToolDefinitionAsync("tool1", CancellationToken.None);

            // Assert
            _loggerMock.Verify(
                x => x.Log(
                    LogLevel.Error,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => true),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);
        }

        [TestMethod]
        public async Task GetToolDefinitionAsync_ShouldReturnToolWithDefaultPortAndPath()
        {
            // Arrange
            var toolName = "test-tool";
            var toolResource = CreateToolResource(toolName); // Uses defaults: 443, "/score"

            _toolResourceStoreMock.Setup(x => x.TryGetAsync(toolName, It.IsAny<CancellationToken>()))
                .ReturnsAsync(toolResource);

            // Act
            var result = await _provider.GetToolDefinitionAsync(toolName, CancellationToken.None);

            // Assert
            result.Should().NotBeNull();
            result!.Port.Should().Be(443);
            result.Path.Should().Be("/score");
        }

        [TestMethod]
        public async Task ListToolsAsync_ShouldReturnMcpTools()
        {
            // Arrange
            var tools = new List<ToolResource>
            {
                CreateToolResource("tool1", "First tool"),
                CreateToolResource("tool2", "Second tool")
            };

            _toolResourceStoreMock.Setup(x => x.ListAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(tools);

            var context = CreateListToolsContext();

            // Act
            var result = await _provider.ListToolsAsync(context, CancellationToken.None);

            // Assert
            result.Tools.Should().HaveCount(2);
            result.Tools.Should().Contain(t => t.Name == "tool1" && t.Description == "First tool");
            result.Tools.Should().Contain(t => t.Name == "tool2" && t.Description == "Second tool");
            result.NextCursor.Should().BeNull();
        }

        [TestMethod]
        public async Task ListToolsAsync_ShouldReturnEmptyList_WhenUserContextMissing()
        {
            // Arrange
            var tools = new List<ToolResource>
            {
                CreateToolResource("tool1")
            };

            _toolResourceStoreMock.Setup(x => x.ListAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(tools);

            _currentHttpContext = null;

            var context = CreateListToolsContext();

            // Act
            var result = await _provider.ListToolsAsync(context, CancellationToken.None);

            // Assert
            result.Tools.Should().BeEmpty();
        }

        [TestMethod]
        public async Task ListToolsAsync_ShouldReturnEmptyList_WhenNoToolsExist()
        {
            // Arrange
            _toolResourceStoreMock.Setup(x => x.ListAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<ToolResource>());

            var context = CreateListToolsContext();

            // Act
            var result = await _provider.ListToolsAsync(context, CancellationToken.None);

            // Assert
            result.Tools.Should().BeEmpty();
            result.NextCursor.Should().BeNull();
        }

        [TestMethod]
        public async Task ListToolsAsync_ShouldHandleStoreError_AndReturnEmptyList()
        {
            // Arrange
            _toolResourceStoreMock.Setup(x => x.ListAsync(It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("Store error"));

            var context = CreateListToolsContext();

            // Act
            var result = await _provider.ListToolsAsync(context, CancellationToken.None);

            // Assert
            result.Tools.Should().BeEmpty();
        }

        [TestMethod]
        public async Task ListToolsAsync_ShouldReturnToolsWithCustomPortAndPath()
        {
            // Arrange
            var tools = new List<ToolResource>
            {
                CreateToolResource("tool1", "First tool", 8080, "/api/execute"),
                CreateToolResource("tool2", "Second tool", 9000, "/custom/path")
            };

            _toolResourceStoreMock.Setup(x => x.ListAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(tools);

            var context = CreateListToolsContext();

            // Act
            var result = await _provider.ListToolsAsync(context, CancellationToken.None);

            // Assert
            result.Tools.Should().HaveCount(2);
            result.Tools.All(t => !string.IsNullOrEmpty(t.Name)).Should().BeTrue();
        }

        [TestMethod]
        public async Task ListToolsAsync_ShouldLogInformation_WhenListingTools()
        {
            // Arrange
            var tools = new List<ToolResource>
            {
                CreateToolResource("tool1")
            };

            _toolResourceStoreMock.Setup(x => x.ListAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(tools);

            var context = CreateListToolsContext();

            // Act
            await _provider.ListToolsAsync(context, CancellationToken.None);

            // Assert
            _loggerMock.Verify(
                x => x.Log(
                    LogLevel.Information,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => true),
                    null,
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.AtLeastOnce);
        }

        [TestMethod]
        public async Task GetToolDefinitionsAsync_ShouldPreserveToolProperties()
        {
            // Arrange
            var customPort = 9000;
            var customPath = "/custom/execute";
            var tools = new List<ToolResource>
            {
                CreateToolResource("tool1", "Custom tool", customPort, customPath)
            };

            _toolResourceStoreMock.Setup(x => x.ListAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(tools);

            // Act
            var result = await _provider.GetToolDefinitionsAsync(CancellationToken.None);

            // Assert
            result.Should().ContainSingle();
            var toolDef = result.First();
            toolDef.Port.Should().Be(customPort);
            toolDef.Path.Should().Be(customPath);
            toolDef.Tool.Description.Should().Be("Custom tool");
        }

        [TestMethod]
        public async Task GetToolDefinitionsAsync_ShouldReturnToolsWithInputSchema()
        {
            // Arrange
            var tools = new List<ToolResource>
            {
                CreateToolResource("tool1")
            };

            _toolResourceStoreMock.Setup(x => x.ListAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(tools);

            // Act
            var result = await _provider.GetToolDefinitionsAsync(CancellationToken.None);

            // Assert
            result.Should().ContainSingle();
            var toolDef = result.First();
            toolDef.Tool.InputSchema.ValueKind.Should().Be(JsonValueKind.Object);
        }

        [TestMethod]
        public void Constructor_ShouldThrowArgumentNullException_WhenToolResourceStoreIsNull()
        {
            // Act
            var act = () => new StorageToolDefinitionProvider(
                null!,
                _permissionProviderMock.Object,
                _httpContextAccessorMock.Object,
                _loggerMock.Object);

            // Assert
            act.Should().Throw<ArgumentNullException>();
        }

        [TestMethod]
        public void Constructor_ShouldThrowArgumentNullException_WhenLoggerIsNull()
        {
            // Act
            var act = () => new StorageToolDefinitionProvider(
                _toolResourceStoreMock.Object,
                _permissionProviderMock.Object,
                _httpContextAccessorMock.Object,
                null!);

            // Assert
            act.Should().Throw<ArgumentNullException>();
        }

        [TestMethod]
        public void Constructor_ShouldThrowArgumentNullException_WhenPermissionProviderIsNull()
        {
            // Act
            var act = () => new StorageToolDefinitionProvider(
                _toolResourceStoreMock.Object,
                null!,
                _httpContextAccessorMock.Object,
                _loggerMock.Object);

            // Assert
            act.Should().Throw<ArgumentNullException>();
        }

        [TestMethod]
        public void Constructor_ShouldThrowArgumentNullException_WhenHttpContextAccessorIsNull()
        {
            // Act
            var act = () => new StorageToolDefinitionProvider(
                _toolResourceStoreMock.Object,
                _permissionProviderMock.Object,
                null!,
                _loggerMock.Object);

            // Assert
            act.Should().Throw<ArgumentNullException>();
        }

        [TestMethod]
        public async Task GetToolDefinitionsAsync_ShouldReturnMultipleToolsWithDifferentConfigurations()
        {
            // Arrange
            var tools = new List<ToolResource>
            {
                CreateToolResource("tool1", "Tool One", 443, "/score"),
                CreateToolResource("tool2", "Tool Two", 8080, "/execute"),
                CreateToolResource("tool3", "Tool Three", 9000, "/api/v1/run")
            };

            _toolResourceStoreMock.Setup(x => x.ListAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(tools);

            // Act
            var result = await _provider.GetToolDefinitionsAsync(CancellationToken.None);

            // Assert
            result.Should().HaveCount(3);
            result.Select(t => t.Tool.Name).Should().BeEquivalentTo(new[] { "tool1", "tool2", "tool3" });
            result.Select(t => t.Port).Should().BeEquivalentTo(new[] { 443, 8080, 9000 });
            result.Select(t => t.Path).Should().BeEquivalentTo(new[] { "/score", "/execute", "/api/v1/run" });
        }
    }
}
