// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.McpGateway.Management.Contracts;
using Microsoft.McpGateway.Management.Deployment;
using Microsoft.McpGateway.Management.Service;
using Moq;

namespace Microsoft.McpGateway.Management.Tests
{
    [TestClass]
    public class AdapterRichResultProviderTests
    {
        private readonly Mock<IAdapterDeploymentManager> _deploymentManagerMock;
        private readonly Mock<ILogger<AdapterManagementService>> _loggerMock;
        private readonly AdapterRichResultProvider _provider;

        public AdapterRichResultProviderTests()
        {
            _deploymentManagerMock = new Mock<IAdapterDeploymentManager>();
            _loggerMock = new Mock<ILogger<AdapterManagementService>>();
            _provider = new AdapterRichResultProvider(_deploymentManagerMock.Object, _loggerMock.Object);
        }

        [TestMethod]
        public async Task GetAdapterLogsAsync_ShouldReturnLogs()
        {
            _deploymentManagerMock.Setup(x => x.GetDeploymentLogsAsync("adapter1", 0, It.IsAny<CancellationToken>())).ReturnsAsync("log-output");

            var result = await _provider.GetAdapterLogsAsync("adapter1", 0, CancellationToken.None);

            result.Should().Be("log-output");
        }

        [TestMethod]
        public async Task GetAdapterStatusAsync_ShouldReturnStatus()
        {
            var status = new AdapterStatus { ReplicaStatus = "Healthy" };
            _deploymentManagerMock.Setup(x => x.GetDeploymentStatusAsync("adapter1", It.IsAny<CancellationToken>())).ReturnsAsync(status);

            var result = await _provider.GetAdapterStatusAsync("adapter1", CancellationToken.None);

            result.Should().Be(status);
        }
    }

}
