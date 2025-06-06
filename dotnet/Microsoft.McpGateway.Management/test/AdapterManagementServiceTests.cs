// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Security.Claims;
using FluentAssertions;
using Microsoft.Extensions.Logging;
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
        private readonly Mock<ILogger<AdapterManagementService>> _loggerMock;
        private readonly AdapterManagementService _service;
        private readonly ClaimsPrincipal _accessContext;

        public AdapterManagementServiceTests()
        {
            _deploymentManagerMock = new Mock<IAdapterDeploymentManager>();
            _storeMock = new Mock<IAdapterResourceStore>();
            _loggerMock = new Mock<ILogger<AdapterManagementService>>();
            _service = new AdapterManagementService(_deploymentManagerMock.Object, _storeMock.Object, _loggerMock.Object);
            _accessContext = new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, "user1")]));
        }

        [TestMethod]
        public async Task CreateAsync_ShouldCreateAdapter_WhenValidRequest()
        {
            var request = new AdapterData { Name = "valid-name", ImageName = "image", ImageVersion = "v1", ReplicaCount = 1, EnvironmentVariables = [] };
            _storeMock.Setup(x => x.TryGetAsync("valid-name", It.IsAny<CancellationToken>())).ReturnsAsync((AdapterResource?)null);

            var result = await _service.CreateAsync(_accessContext, request, CancellationToken.None);

            result.Name.Should().Be("valid-name");
            _deploymentManagerMock.Verify(x => x.CreateDeploymentAsync(It.IsAny<AdapterResource>(), It.IsAny<CancellationToken>()), Times.Once);
            _storeMock.Verify(x => x.UpsertAsync(It.IsAny<AdapterResource>(), It.IsAny<CancellationToken>()), Times.Once);
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
        public async Task UpdateAsync_ShouldUpdate_WhenValidRequest()
        {
            var existing = AdapterResource.Create(new AdapterData { Name = "adapter1", ImageName = "old", ImageVersion = "v1", ReplicaCount = 1, EnvironmentVariables = [] }, "user1", DateTimeOffset.UtcNow);
            var updatedRequest = new AdapterData { Name = "adapter1", ImageName = "new", ImageVersion = "v1", ReplicaCount = 1, EnvironmentVariables = [] };
            _storeMock.Setup(x => x.TryGetAsync("adapter1", It.IsAny<CancellationToken>())).ReturnsAsync(existing);

            var result = await _service.UpdateAsync(_accessContext, updatedRequest, CancellationToken.None);

            result.ImageName.Should().Be("new");
            _deploymentManagerMock.Verify(x => x.UpdateDeploymentAsync(It.IsAny<AdapterResource>(), It.IsAny<CancellationToken>()), Times.Once);
            _storeMock.Verify(x => x.UpsertAsync(It.IsAny<AdapterResource>(), It.IsAny<CancellationToken>()), Times.Once);
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
    }

}
