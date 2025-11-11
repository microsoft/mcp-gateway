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
using Moq;

namespace Microsoft.McpGateway.Management.Tests
{
    [TestClass]
    public class AdapterManagementServiceTests
    {
        private readonly Mock<IAdapterDeploymentManager> _deploymentManagerMock;
        private readonly Mock<IAdapterResourceStore> _storeMock;
        private readonly Mock<IPermissionProvider> _permissionProviderMock;
        private readonly Mock<ILogger<AdapterManagementService>> _loggerMock;
    private readonly AdapterManagementService _service;
        private readonly ClaimsPrincipal _accessContext;

        public AdapterManagementServiceTests()
        {
            _deploymentManagerMock = new Mock<IAdapterDeploymentManager>();
            _storeMock = new Mock<IAdapterResourceStore>();
            _permissionProviderMock = new Mock<IPermissionProvider>();
            _loggerMock = new Mock<ILogger<AdapterManagementService>>();
            _permissionProviderMock.Setup(x => x.CheckAccessAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<IManagedResource>(), Operation.Read))
                .ReturnsAsync(true);
            _permissionProviderMock.Setup(x => x.CheckAccessAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<IManagedResource>(), Operation.Write))
                .ReturnsAsync(true);
            _permissionProviderMock.Setup(x => x.CheckAccessAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<IEnumerable<AdapterResource>>(), Operation.Read))
                .ReturnsAsync((ClaimsPrincipal _, IEnumerable<AdapterResource> resources, Operation _) => resources.ToArray());
            _service = new AdapterManagementService(_deploymentManagerMock.Object, _storeMock.Object, _permissionProviderMock.Object, _loggerMock.Object);
            _accessContext = new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, "user1")]));
        }

        [TestMethod]
        public async Task CreateAsync_ShouldCreateAdapter_WhenValidRequest()
        {
            var request = new AdapterData { Name = "valid-name", ImageName = "image", ImageVersion = "v1", ReplicaCount = 1, EnvironmentVariables = [] };
            _storeMock.Setup(x => x.TryGetAsync("valid-name", It.IsAny<CancellationToken>())).ReturnsAsync((AdapterResource?)null);

            var result = await _service.CreateAsync(_accessContext, request, CancellationToken.None);

            result.Name.Should().Be("valid-name");
            _deploymentManagerMock.Verify(x => x.CreateDeploymentAsync(It.IsAny<AdapterData>(), ResourceType.Mcp, It.IsAny<CancellationToken>()), Times.Once);
            _storeMock.Verify(x => x.UpsertAsync(It.IsAny<AdapterResource>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task CreateAsync_ShouldThrowArgumentException_WhenNameIsInvalid()
        {
            var request = new AdapterData { Name = "Invalid_Name", ImageName = "image", ImageVersion = "v1", ReplicaCount = 1 };

            Func<Task> act = () => _service.CreateAsync(_accessContext, request, CancellationToken.None);

            await act.Should().ThrowAsync<ArgumentException>()
                .WithMessage("Name must contain only lowercase letters, numbers, and dashes.");
        }

        [TestMethod]
        public async Task CreateAsync_ShouldThrowArgumentException_WhenAdapterAlreadyExists()
        {
            var request = new AdapterData { Name = "existing-adapter", ImageName = "image", ImageVersion = "v1", ReplicaCount = 1 };
            var existing = AdapterResource.Create(request, "user1", DateTimeOffset.UtcNow);
            _storeMock.Setup(x => x.TryGetAsync("existing-adapter", It.IsAny<CancellationToken>())).ReturnsAsync(existing);

            Func<Task> act = () => _service.CreateAsync(_accessContext, request, CancellationToken.None);

            await act.Should().ThrowAsync<ArgumentException>()
                .WithMessage("The adapter with the same name already exist.");
        }

        [TestMethod]
        public async Task CreateAsync_ShouldThrowArgumentNullException_WhenAccessContextIsNull()
        {
            var request = new AdapterData { Name = "adapter1", ImageName = "image", ImageVersion = "v1" };

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
        public async Task GetAsync_ShouldReturnAdapter_WhenExists()
        {
            var adapter = AdapterResource.Create(new AdapterData { Name = "adapter1", ImageName = "image", ImageVersion = "v1", ReplicaCount = 1, EnvironmentVariables = [] }, "user1", DateTimeOffset.UtcNow);
            _storeMock.Setup(x => x.TryGetAsync("adapter1", It.IsAny<CancellationToken>())).ReturnsAsync(adapter);

            var result = await _service.GetAsync(_accessContext, "adapter1", CancellationToken.None);

            result.Should().Be(adapter);
        }

        [TestMethod]
        public async Task GetAsync_ShouldReturnNull_WhenAdapterDoesNotExist()
        {
            _storeMock.Setup(x => x.TryGetAsync("nonexistent", It.IsAny<CancellationToken>())).ReturnsAsync((AdapterResource?)null);

            var result = await _service.GetAsync(_accessContext, "nonexistent", CancellationToken.None);

            result.Should().BeNull();
        }

        [TestMethod]
        public async Task GetAsync_ShouldThrowUnauthorizedAccessException_WhenUserIsNotAllowed()
        {
            var adapter = AdapterResource.Create(new AdapterData { Name = "restricted", ImageName = "image", ImageVersion = "v1" }, "differentUser", DateTimeOffset.UtcNow);
            _storeMock.Setup(x => x.TryGetAsync("restricted", It.IsAny<CancellationToken>())).ReturnsAsync(adapter);
            _permissionProviderMock.Setup(x => x.CheckAccessAsync(
                It.IsAny<ClaimsPrincipal>(),
                It.Is<IManagedResource>(resource => resource.Name == adapter.Name),
                Operation.Read)).ReturnsAsync(false);

            Func<Task> act = () => _service.GetAsync(_accessContext, "restricted", CancellationToken.None);

            await act.Should().ThrowAsync<UnauthorizedAccessException>()
                .WithMessage("You do not have permission to perform the operation.");
        }

        [TestMethod]
        public async Task GetAsync_ShouldThrowArgumentNullException_WhenAccessContextIsNull()
        {
            Func<Task> act = () => _service.GetAsync(null!, "adapter1", CancellationToken.None);

            await act.Should().ThrowAsync<ArgumentNullException>();
        }

        [TestMethod]
        public async Task GetAsync_ShouldThrowArgumentException_WhenNameIsEmpty()
        {
            Func<Task> act = () => _service.GetAsync(_accessContext, "", CancellationToken.None);

            await act.Should().ThrowAsync<ArgumentException>();
        }

        [TestMethod]
        public async Task UpdateAsync_ShouldUpdate_WhenValidRequest()
        {
            var existing = AdapterResource.Create(new AdapterData { Name = "adapter1", ImageName = "old", ImageVersion = "v1", ReplicaCount = 1, EnvironmentVariables = [] }, "user1", DateTimeOffset.UtcNow);
            var updatedRequest = new AdapterData { Name = "adapter1", ImageName = "new", ImageVersion = "v1", ReplicaCount = 1, EnvironmentVariables = [] };
            _storeMock.Setup(x => x.TryGetAsync("adapter1", It.IsAny<CancellationToken>())).ReturnsAsync(existing);

            var result = await _service.UpdateAsync(_accessContext, updatedRequest, CancellationToken.None);

            result.ImageName.Should().Be("new");
            _deploymentManagerMock.Verify(x => x.UpdateDeploymentAsync(It.IsAny<AdapterResource>(), ResourceType.Mcp, It.IsAny<CancellationToken>()), Times.Once);
            _storeMock.Verify(x => x.UpsertAsync(It.IsAny<AdapterResource>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task UpdateAsync_ShouldNotTriggerDeployment_WhenNoDeploymentChanges()
        {
            var existing = AdapterResource.Create(
                new AdapterData 
                { 
                    Name = "adapter1", 
                    ImageName = "image", 
                    ImageVersion = "v1", 
                    ReplicaCount = 1, 
                    EnvironmentVariables = new Dictionary<string, string> { { "KEY", "value" } },
                    Description = "Old description"
                }, 
                "user1", 
                DateTimeOffset.UtcNow);
            var updatedRequest = new AdapterData 
            { 
                Name = "adapter1", 
                ImageName = "image", 
                ImageVersion = "v1", 
                ReplicaCount = 1,
                EnvironmentVariables = new Dictionary<string, string> { { "KEY", "value" } },
                Description = "New description"
            };
            _storeMock.Setup(x => x.TryGetAsync("adapter1", It.IsAny<CancellationToken>())).ReturnsAsync(existing);

            var result = await _service.UpdateAsync(_accessContext, updatedRequest, CancellationToken.None);

            result.Description.Should().Be("New description");
            _deploymentManagerMock.Verify(x => x.UpdateDeploymentAsync(It.IsAny<AdapterResource>(), ResourceType.Mcp, It.IsAny<CancellationToken>()), Times.Never);
            _storeMock.Verify(x => x.UpsertAsync(It.IsAny<AdapterResource>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task UpdateAsync_ShouldTriggerDeployment_WhenReplicaCountChanges()
        {
            var existing = AdapterResource.Create(
                new AdapterData { Name = "adapter1", ImageName = "image", ImageVersion = "v1", ReplicaCount = 1 }, 
                "user1", 
                DateTimeOffset.UtcNow);
            var updatedRequest = new AdapterData { Name = "adapter1", ImageName = "image", ImageVersion = "v1", ReplicaCount = 3 };
            _storeMock.Setup(x => x.TryGetAsync("adapter1", It.IsAny<CancellationToken>())).ReturnsAsync(existing);

            await _service.UpdateAsync(_accessContext, updatedRequest, CancellationToken.None);

            _deploymentManagerMock.Verify(x => x.UpdateDeploymentAsync(It.IsAny<AdapterResource>(), ResourceType.Mcp, It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task UpdateAsync_ShouldTriggerDeployment_WhenEnvironmentVariablesChange()
        {
            var existing = AdapterResource.Create(
                new AdapterData 
                { 
                    Name = "adapter1", 
                    ImageName = "image", 
                    ImageVersion = "v1", 
                    ReplicaCount = 1,
                    EnvironmentVariables = new Dictionary<string, string> { { "KEY", "oldvalue" } }
                }, 
                "user1", 
                DateTimeOffset.UtcNow);
            var updatedRequest = new AdapterData 
            { 
                Name = "adapter1", 
                ImageName = "image", 
                ImageVersion = "v1", 
                ReplicaCount = 1,
                EnvironmentVariables = new Dictionary<string, string> { { "KEY", "newvalue" } }
            };
            _storeMock.Setup(x => x.TryGetAsync("adapter1", It.IsAny<CancellationToken>())).ReturnsAsync(existing);

            await _service.UpdateAsync(_accessContext, updatedRequest, CancellationToken.None);

            _deploymentManagerMock.Verify(x => x.UpdateDeploymentAsync(It.IsAny<AdapterResource>(), ResourceType.Mcp, It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task UpdateAsync_ShouldThrowArgumentException_WhenAdapterDoesNotExist()
        {
            var request = new AdapterData { Name = "nonexistent", ImageName = "image", ImageVersion = "v1" };
            _storeMock.Setup(x => x.TryGetAsync("nonexistent", It.IsAny<CancellationToken>())).ReturnsAsync((AdapterResource?)null);

            Func<Task> act = () => _service.UpdateAsync(_accessContext, request, CancellationToken.None);

            await act.Should().ThrowAsync<ArgumentException>()
                .WithMessage("The adapter does not exist and cannot be updated.");
        }

        [TestMethod]
        public async Task UpdateAsync_ShouldThrowUnauthorizedAccessException_WhenUserIsNotOwner()
        {
            var existing = AdapterResource.Create(
                new AdapterData { Name = "adapter1", ImageName = "image", ImageVersion = "v1" }, 
                "differentUser", 
                DateTimeOffset.UtcNow);
            var request = new AdapterData { Name = "adapter1", ImageName = "new-image", ImageVersion = "v2" };
            _storeMock.Setup(x => x.TryGetAsync("adapter1", It.IsAny<CancellationToken>())).ReturnsAsync(existing);
            _permissionProviderMock.Setup(x => x.CheckAccessAsync(
                It.IsAny<ClaimsPrincipal>(),
                It.Is<IManagedResource>(resource => resource.Name == existing.Name),
                Operation.Write)).ReturnsAsync(false);

            Func<Task> act = () => _service.UpdateAsync(_accessContext, request, CancellationToken.None);

            await act.Should().ThrowAsync<UnauthorizedAccessException>()
                .WithMessage("You do not have permission to perform the operation.");
        }

        [TestMethod]
        public async Task DeleteAsync_ShouldDeleteAdapter()
        {
            var existing = AdapterResource.Create(new AdapterData { Name = "adapter1", ImageName = "image", ImageVersion = "v1", ReplicaCount = 1, EnvironmentVariables = [] }, "user1", DateTimeOffset.UtcNow);
            _storeMock.Setup(x => x.TryGetAsync("adapter1", It.IsAny<CancellationToken>())).ReturnsAsync(existing);

            await _service.DeleteAsync(_accessContext, "adapter1", CancellationToken.None);

            _storeMock.Verify(x => x.DeleteAsync("adapter1", It.IsAny<CancellationToken>()), Times.Once);
            _deploymentManagerMock.Verify(x => x.DeleteDeploymentAsync("adapter1", It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task DeleteAsync_ShouldThrowArgumentException_WhenAdapterDoesNotExist()
        {
            _storeMock.Setup(x => x.TryGetAsync("nonexistent", It.IsAny<CancellationToken>())).ReturnsAsync((AdapterResource?)null);

            Func<Task> act = () => _service.DeleteAsync(_accessContext, "nonexistent", CancellationToken.None);

            await act.Should().ThrowAsync<ArgumentException>()
                .WithMessage("The adapter does not exist.");
        }

        [TestMethod]
        public async Task DeleteAsync_ShouldThrowUnauthorizedAccessException_WhenUserIsNotOwner()
        {
            var existing = AdapterResource.Create(
                new AdapterData { Name = "adapter1", ImageName = "image", ImageVersion = "v1" }, 
                "differentUser", 
                DateTimeOffset.UtcNow);
            _storeMock.Setup(x => x.TryGetAsync("adapter1", It.IsAny<CancellationToken>())).ReturnsAsync(existing);
            _permissionProviderMock.Setup(x => x.CheckAccessAsync(
                It.IsAny<ClaimsPrincipal>(),
                It.Is<IManagedResource>(resource => resource.Name == existing.Name),
                Operation.Write)).ReturnsAsync(false);

            Func<Task> act = () => _service.DeleteAsync(_accessContext, "adapter1", CancellationToken.None);

            await act.Should().ThrowAsync<UnauthorizedAccessException>()
                .WithMessage("You do not have permission to perform the operation.");
        }

        [TestMethod]
        public async Task DeleteAsync_ShouldThrowArgumentNullException_WhenAccessContextIsNull()
        {
            Func<Task> act = () => _service.DeleteAsync(null!, "adapter1", CancellationToken.None);

            await act.Should().ThrowAsync<ArgumentNullException>();
        }

        [TestMethod]
        public async Task DeleteAsync_ShouldThrowArgumentException_WhenNameIsEmpty()
        {
            Func<Task> act = () => _service.DeleteAsync(_accessContext, "", CancellationToken.None);

            await act.Should().ThrowAsync<ArgumentException>();
        }

        [TestMethod]
        public async Task ListAsync_ShouldReturnResources()
        {
            var resources = new List<AdapterResource>
            {
                AdapterResource.Create(new AdapterData { Name = "adapter1", ImageName = "image", ImageVersion = "v1", ReplicaCount = 1, EnvironmentVariables = [] }, "user1", DateTimeOffset.UtcNow)
            };
            _storeMock.Setup(x => x.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(resources);

            var result = await _service.ListAsync(_accessContext, CancellationToken.None);

            result.Should().BeEquivalentTo(resources);
        }

        [TestMethod]
        public async Task ListAsync_ShouldFilterUnauthorizedAdapters()
        {
            var adapter1 = AdapterResource.Create(new AdapterData { Name = "adapter1", ImageName = "image", ImageVersion = "v1" }, "user1", DateTimeOffset.UtcNow);
            var adapter2 = AdapterResource.Create(new AdapterData { Name = "adapter2", ImageName = "image", ImageVersion = "v1" }, "user2", DateTimeOffset.UtcNow);
            _storeMock.Setup(x => x.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync([adapter1, adapter2]);
            _permissionProviderMock.Setup(x => x.CheckAccessAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<IEnumerable<AdapterResource>>(), Operation.Read))
                .ReturnsAsync((ClaimsPrincipal _, IEnumerable<AdapterResource> resources, Operation _) => resources.Where(r => r.Name == adapter1.Name).ToArray());

            var result = await _service.ListAsync(_accessContext, CancellationToken.None);

            result.Should().BeEquivalentTo(new[] { adapter1 });
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
            var act = () => new AdapterManagementService(null!, _storeMock.Object, _permissionProviderMock.Object, _loggerMock.Object);

            act.Should().Throw<ArgumentNullException>().WithParameterName("adapterDeploymentManager");
        }

        [TestMethod]
        public void Constructor_ShouldThrowArgumentNullException_WhenStoreIsNull()
        {
            var act = () => new AdapterManagementService(_deploymentManagerMock.Object, null!, _permissionProviderMock.Object, _loggerMock.Object);

            act.Should().Throw<ArgumentNullException>().WithParameterName("store");
        }

        [TestMethod]
        public void Constructor_ShouldThrowArgumentNullException_WhenPermissionProviderIsNull()
        {
            var act = () => new AdapterManagementService(_deploymentManagerMock.Object, _storeMock.Object, null!, _loggerMock.Object);

            act.Should().Throw<ArgumentNullException>().WithParameterName("permissionProvider");
        }

        [TestMethod]
        public void Constructor_ShouldThrowArgumentNullException_WhenLoggerIsNull()
        {
            var act = () => new AdapterManagementService(_deploymentManagerMock.Object, _storeMock.Object, _permissionProviderMock.Object, null!);

            act.Should().Throw<ArgumentNullException>().WithParameterName("logger");
        }
    }

}
