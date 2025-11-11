// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.McpGateway.Management.Authorization;
using Microsoft.McpGateway.Management.Contracts;
using Microsoft.McpGateway.Management.Deployment;
using Microsoft.McpGateway.Management.Service;
using Microsoft.McpGateway.Management.Store;
using ModelContextProtocol.Protocol;
using Moq;

namespace Microsoft.McpGateway.Management.Tests
{
    [TestClass]
    public class ToolManagementServiceTests
    {
        private readonly Mock<IAdapterDeploymentManager> _deploymentManagerMock;
        private readonly Mock<IToolResourceStore> _storeMock;
        private readonly Mock<IPermissionProvider> _permissionProviderMock;
        private readonly Mock<ILogger<ToolManagementService>> _loggerMock;
        private readonly ToolManagementService _service;
        private readonly ClaimsPrincipal _accessContext;

        public ToolManagementServiceTests()
        {
            _deploymentManagerMock = new Mock<IAdapterDeploymentManager>();
            _storeMock = new Mock<IToolResourceStore>();
            _permissionProviderMock = new Mock<IPermissionProvider>();
            _loggerMock = new Mock<ILogger<ToolManagementService>>();
            _permissionProviderMock.Setup(x => x.CheckAccessAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<IManagedResource>(), Operation.Read))
                .ReturnsAsync(true);
            _permissionProviderMock.Setup(x => x.CheckAccessAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<IManagedResource>(), Operation.Write))
                .ReturnsAsync(true);
            _permissionProviderMock.Setup(x => x.CheckAccessAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<IEnumerable<ToolResource>>(), Operation.Read))
                .ReturnsAsync((ClaimsPrincipal _, IEnumerable<ToolResource> resources, Operation _) => resources.ToArray());
            _service = new ToolManagementService(_deploymentManagerMock.Object, _storeMock.Object, _permissionProviderMock.Object, _loggerMock.Object);
            _accessContext = new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, "user1")]));
        }

        private static ToolData CreateToolData(string name = "test-tool", string imageName = "test-image", string imageVersion = "v1")
        {
            var inputSchemaJson = System.Text.Json.JsonSerializer.Serialize(new { type = "object", properties = new { } });
            var inputSchema = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(inputSchemaJson);
            
            return new ToolData
            {
                Name = name,
                ImageName = imageName,
                ImageVersion = imageVersion,
                ReplicaCount = 1,
                EnvironmentVariables = [],
                ToolDefinition = new ToolDefinition
                {
                    Tool = new Tool
                    {
                        Name = name,
                        Description = "Test tool",
                        InputSchema = inputSchema
                    }
                }
            };
        }

        [TestMethod]
        public async Task CreateAsync_ShouldCreateTool_WhenValidRequest()
        {
            var request = CreateToolData("valid-tool", "tool-image", "v1");
            _storeMock.Setup(x => x.TryGetAsync("valid-tool", It.IsAny<CancellationToken>())).ReturnsAsync((ToolResource?)null);

            var result = await _service.CreateAsync(_accessContext, request, CancellationToken.None);

            result.Name.Should().Be("valid-tool");
            _deploymentManagerMock.Verify(x => x.CreateDeploymentAsync(It.IsAny<AdapterData>(), ResourceType.Tool, It.IsAny<CancellationToken>()), Times.Once);
            _storeMock.Verify(x => x.UpsertAsync(It.IsAny<ToolResource>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task CreateAsync_ShouldThrowArgumentException_WhenNameIsInvalid()
        {
            var request = CreateToolData("Invalid_Name", "image", "v1");

            Func<Task> act = () => _service.CreateAsync(_accessContext, request, CancellationToken.None);

            await act.Should().ThrowAsync<ArgumentException>()
                .WithMessage("Name must contain only lowercase letters, numbers, and dashes.");
        }

        [TestMethod]
        public async Task CreateAsync_ShouldThrowArgumentException_WhenToolAlreadyExists()
        {
            var request = CreateToolData("existing-tool", "image", "v1");
            var existing = ToolResource.Create(request, "user1", DateTimeOffset.UtcNow);
            _storeMock.Setup(x => x.TryGetAsync("existing-tool", It.IsAny<CancellationToken>())).ReturnsAsync(existing);

            Func<Task> act = () => _service.CreateAsync(_accessContext, request, CancellationToken.None);

            await act.Should().ThrowAsync<ArgumentException>()
                .WithMessage("The tool with the same name already exists.");
        }

        [TestMethod]
        public async Task CreateAsync_ShouldThrowArgumentNullException_WhenAccessContextIsNull()
        {
            var request = CreateToolData();

            Func<Task> act = () => _service.CreateAsync(null!, request, CancellationToken.None);

            await act.Should().ThrowAsync<ArgumentNullException>();
        }

        [TestMethod]
        public async Task CreateAsync_ShouldThrowArgumentNullException_WhenRequestIsNull()
        {
            Func<Task> act = () => _service.CreateAsync(_accessContext, null!, CancellationToken.None);

            await act.Should().ThrowAsync<ArgumentNullException>();
        }

        [TestMethod]
        public async Task GetAsync_ShouldReturnTool_WhenExists()
        {
            var tool = ToolResource.Create(CreateToolData("tool1"), "user1", DateTimeOffset.UtcNow);
            _storeMock.Setup(x => x.TryGetAsync("tool1", It.IsAny<CancellationToken>())).ReturnsAsync(tool);

            var result = await _service.GetAsync(_accessContext, "tool1", CancellationToken.None);

            result.Should().Be(tool);
        }

        [TestMethod]
        public async Task GetAsync_ShouldReturnNull_WhenToolDoesNotExist()
        {
            _storeMock.Setup(x => x.TryGetAsync("nonexistent", It.IsAny<CancellationToken>())).ReturnsAsync((ToolResource?)null);

            var result = await _service.GetAsync(_accessContext, "nonexistent", CancellationToken.None);

            result.Should().BeNull();
        }

        [TestMethod]
        public async Task GetAsync_ShouldThrowUnauthorizedAccessException_WhenUserIsNotAllowed()
        {
            var tool = ToolResource.Create(CreateToolData("restricted"), "differentUser", DateTimeOffset.UtcNow);
            _storeMock.Setup(x => x.TryGetAsync("restricted", It.IsAny<CancellationToken>())).ReturnsAsync(tool);
            _permissionProviderMock.Setup(x => x.CheckAccessAsync(
                It.IsAny<ClaimsPrincipal>(),
                It.Is<IManagedResource>(resource => resource.Name == tool.Name),
                Operation.Read)).ReturnsAsync(false);

            Func<Task> act = () => _service.GetAsync(_accessContext, "restricted", CancellationToken.None);

            await act.Should().ThrowAsync<UnauthorizedAccessException>()
                .WithMessage("You do not have permission to perform the operation.");
        }

        [TestMethod]
        public async Task GetAsync_ShouldThrowArgumentNullException_WhenAccessContextIsNull()
        {
            Func<Task> act = () => _service.GetAsync(null!, "tool1", CancellationToken.None);

            await act.Should().ThrowAsync<ArgumentNullException>();
        }

        [TestMethod]
        public async Task GetAsync_ShouldThrowArgumentException_WhenNameIsEmpty()
        {
            Func<Task> act = () => _service.GetAsync(_accessContext, "", CancellationToken.None);

            await act.Should().ThrowAsync<ArgumentException>();
        }

        [TestMethod]
        public async Task UpdateAsync_ShouldUpdateTool_WhenValidRequest()
        {
            var existing = ToolResource.Create(CreateToolData("tool1", "old-image", "v1"), "user1", DateTimeOffset.UtcNow);
            var updatedRequest = CreateToolData("tool1", "new-image", "v2");
            updatedRequest.ReplicaCount = 2;
            _storeMock.Setup(x => x.TryGetAsync("tool1", It.IsAny<CancellationToken>())).ReturnsAsync(existing);

            var result = await _service.UpdateAsync(_accessContext, updatedRequest, CancellationToken.None);

            result.ImageName.Should().Be("new-image");
            result.ImageVersion.Should().Be("v2");
            result.ReplicaCount.Should().Be(2);
            _deploymentManagerMock.Verify(x => x.UpdateDeploymentAsync(It.IsAny<AdapterData>(), ResourceType.Tool, It.IsAny<CancellationToken>()), Times.Once);
            _storeMock.Verify(x => x.UpsertAsync(It.IsAny<ToolResource>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task UpdateAsync_ShouldNotTriggerDeployment_WhenNoDeploymentChanges()
        {
            var request1 = CreateToolData("tool1", "image", "v1");
            request1.Description = "Old description";
            request1.EnvironmentVariables = new Dictionary<string, string> { { "KEY", "value" } };
            var existing = ToolResource.Create(request1, "user1", DateTimeOffset.UtcNow);
            
            var updatedRequest = CreateToolData("tool1", "image", "v1");
            updatedRequest.EnvironmentVariables = new Dictionary<string, string> { { "KEY", "value" } };
            updatedRequest.Description = "New description";
            
            _storeMock.Setup(x => x.TryGetAsync("tool1", It.IsAny<CancellationToken>())).ReturnsAsync(existing);

            var result = await _service.UpdateAsync(_accessContext, updatedRequest, CancellationToken.None);

            result.Description.Should().Be("New description");
            _deploymentManagerMock.Verify(x => x.UpdateDeploymentAsync(It.IsAny<AdapterData>(), ResourceType.Tool, It.IsAny<CancellationToken>()), Times.Never);
            _storeMock.Verify(x => x.UpsertAsync(It.IsAny<ToolResource>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task UpdateAsync_ShouldTriggerDeployment_WhenEnvironmentVariablesChange()
        {
            var request1 = CreateToolData("tool1", "image", "v1");
            request1.EnvironmentVariables = new Dictionary<string, string> { { "KEY", "oldvalue" } };
            var existing = ToolResource.Create(request1, "user1", DateTimeOffset.UtcNow);
            
            var updatedRequest = CreateToolData("tool1", "image", "v1");
            updatedRequest.EnvironmentVariables = new Dictionary<string, string> { { "KEY", "newvalue" } };
            
            _storeMock.Setup(x => x.TryGetAsync("tool1", It.IsAny<CancellationToken>())).ReturnsAsync(existing);

            await _service.UpdateAsync(_accessContext, updatedRequest, CancellationToken.None);

            _deploymentManagerMock.Verify(x => x.UpdateDeploymentAsync(It.IsAny<AdapterData>(), ResourceType.Tool, It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task UpdateAsync_ShouldThrowArgumentException_WhenToolDoesNotExist()
        {
            var request = CreateToolData("nonexistent");
            _storeMock.Setup(x => x.TryGetAsync("nonexistent", It.IsAny<CancellationToken>())).ReturnsAsync((ToolResource?)null);

            Func<Task> act = () => _service.UpdateAsync(_accessContext, request, CancellationToken.None);

            await act.Should().ThrowAsync<ArgumentException>()
                .WithMessage("The tool does not exist and cannot be updated.");
        }

        [TestMethod]
        public async Task UpdateAsync_ShouldThrowUnauthorizedAccessException_WhenUserIsNotOwner()
        {
            var existing = ToolResource.Create(CreateToolData("tool1", "image", "v1"), "differentUser", DateTimeOffset.UtcNow);
            var request = CreateToolData("tool1", "new-image", "v2");
            _storeMock.Setup(x => x.TryGetAsync("tool1", It.IsAny<CancellationToken>())).ReturnsAsync(existing);
            _permissionProviderMock.Setup(x => x.CheckAccessAsync(
                It.IsAny<ClaimsPrincipal>(),
                It.Is<IManagedResource>(resource => resource.Name == existing.Name),
                Operation.Write)).ReturnsAsync(false);

            Func<Task> act = () => _service.UpdateAsync(_accessContext, request, CancellationToken.None);

            await act.Should().ThrowAsync<UnauthorizedAccessException>()
                .WithMessage("You do not have permission to perform the operation.");
        }

        [TestMethod]
        public async Task DeleteAsync_ShouldDeleteTool()
        {
            var existing = ToolResource.Create(CreateToolData("tool1"), "user1", DateTimeOffset.UtcNow);
            _storeMock.Setup(x => x.TryGetAsync("tool1", It.IsAny<CancellationToken>())).ReturnsAsync(existing);

            await _service.DeleteAsync(_accessContext, "tool1", CancellationToken.None);

            _storeMock.Verify(x => x.DeleteAsync("tool1", It.IsAny<CancellationToken>()), Times.Once);
            _deploymentManagerMock.Verify(x => x.DeleteDeploymentAsync("tool1", It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task DeleteAsync_ShouldThrowArgumentException_WhenToolDoesNotExist()
        {
            _storeMock.Setup(x => x.TryGetAsync("nonexistent", It.IsAny<CancellationToken>())).ReturnsAsync((ToolResource?)null);

            Func<Task> act = () => _service.DeleteAsync(_accessContext, "nonexistent", CancellationToken.None);

            await act.Should().ThrowAsync<ArgumentException>()
                .WithMessage("The tool does not exist.");
        }

        [TestMethod]
        public async Task DeleteAsync_ShouldThrowUnauthorizedAccessException_WhenUserIsNotOwner()
        {
            var existing = ToolResource.Create(CreateToolData("tool1"), "differentUser", DateTimeOffset.UtcNow);
            _storeMock.Setup(x => x.TryGetAsync("tool1", It.IsAny<CancellationToken>())).ReturnsAsync(existing);
            _permissionProviderMock.Setup(x => x.CheckAccessAsync(
                It.IsAny<ClaimsPrincipal>(),
                It.Is<IManagedResource>(resource => resource.Name == existing.Name),
                Operation.Write)).ReturnsAsync(false);

            Func<Task> act = () => _service.DeleteAsync(_accessContext, "tool1", CancellationToken.None);

            await act.Should().ThrowAsync<UnauthorizedAccessException>()
                .WithMessage("You do not have permission to perform the operation.");
        }

        [TestMethod]
        public async Task DeleteAsync_ShouldThrowArgumentNullException_WhenAccessContextIsNull()
        {
            Func<Task> act = () => _service.DeleteAsync(null!, "tool1", CancellationToken.None);

            await act.Should().ThrowAsync<ArgumentNullException>();
        }

        [TestMethod]
        public async Task DeleteAsync_ShouldThrowArgumentException_WhenNameIsEmpty()
        {
            Func<Task> act = () => _service.DeleteAsync(_accessContext, "", CancellationToken.None);

            await act.Should().ThrowAsync<ArgumentException>();
        }

        [TestMethod]
        public async Task ListAsync_ShouldReturnAllTools()
        {
            var tools = new List<ToolResource>
            {
                ToolResource.Create(CreateToolData("tool1", "image1", "v1"), "user1", DateTimeOffset.UtcNow),
                ToolResource.Create(CreateToolData("tool2", "image2", "v1"), "user2", DateTimeOffset.UtcNow)
            };
            _storeMock.Setup(x => x.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(tools);

            var result = await _service.ListAsync(_accessContext, CancellationToken.None);

            result.Should().BeEquivalentTo(tools);
        }

        [TestMethod]
        public async Task ListAsync_ShouldFilterUnauthorizedTools()
        {
            var tool1 = ToolResource.Create(CreateToolData("tool1", "image1", "v1"), "user1", DateTimeOffset.UtcNow);
            var tool2 = ToolResource.Create(CreateToolData("tool2", "image2", "v1"), "user2", DateTimeOffset.UtcNow);
            _storeMock.Setup(x => x.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync([tool1, tool2]);
            _permissionProviderMock.Setup(x => x.CheckAccessAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<IEnumerable<ToolResource>>(), Operation.Read))
                .ReturnsAsync((ClaimsPrincipal _, IEnumerable<ToolResource> resources, Operation _) => resources.Where(r => r.Name == tool1.Name).ToArray());

            var result = await _service.ListAsync(_accessContext, CancellationToken.None);

            result.Should().BeEquivalentTo(new[] { tool1 });
        }

        [TestMethod]
        public async Task ListAsync_ShouldThrowArgumentNullException_WhenAccessContextIsNull()
        {
            Func<Task> act = () => _service.ListAsync(null!, CancellationToken.None);

            await act.Should().ThrowAsync<ArgumentNullException>();
        }

        [TestMethod]
        public void Constructor_ShouldThrowArgumentNullException_WhenDeploymentManagerIsNull()
        {
            var act = () => new ToolManagementService(null!, _storeMock.Object, _permissionProviderMock.Object, _loggerMock.Object);

            act.Should().Throw<ArgumentNullException>().WithParameterName("adapterDeploymentManager");
        }

        [TestMethod]
        public void Constructor_ShouldThrowArgumentNullException_WhenStoreIsNull()
        {
            var act = () => new ToolManagementService(_deploymentManagerMock.Object, null!, _permissionProviderMock.Object, _loggerMock.Object);

            act.Should().Throw<ArgumentNullException>().WithParameterName("store");
        }

        [TestMethod]
        public void Constructor_ShouldThrowArgumentNullException_WhenPermissionProviderIsNull()
        {
            var act = () => new ToolManagementService(_deploymentManagerMock.Object, _storeMock.Object, null!, _loggerMock.Object);

            act.Should().Throw<ArgumentNullException>().WithParameterName("permissionProvider");
        }

        [TestMethod]
        public void Constructor_ShouldThrowArgumentNullException_WhenLoggerIsNull()
        {
            var act = () => new ToolManagementService(_deploymentManagerMock.Object, _storeMock.Object, _permissionProviderMock.Object, null!);

            act.Should().Throw<ArgumentNullException>().WithParameterName("logger");
        }
    }
}
