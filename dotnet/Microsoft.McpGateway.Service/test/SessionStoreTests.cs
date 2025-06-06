// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.McpGateway.Service.Session;
using Moq;

namespace Microsoft.McpGateway.Service.Tests
{
    [TestClass]
    public class SessionStoreTests
    {
        private readonly Mock<IDistributedCache> _distributedCacheMock;
        private readonly DistributedMemorySessionStore _sessionStore;

        public SessionStoreTests()
        {
            _distributedCacheMock = new Mock<IDistributedCache>();
            _sessionStore = new DistributedMemorySessionStore(_distributedCacheMock.Object, NullLogger<DistributedMemorySessionStore>.Instance);
        }

        [TestMethod]
        public async Task GetAsync_ReturnsFromInMemoryCache_IfExists()
        {
            var sessionId = "s1";
            var target = "http://target";
            var cancellationToken = CancellationToken.None;

            await _sessionStore.SetAsync(sessionId, target, cancellationToken);

            var result = await _sessionStore.TryGetAsync(sessionId, cancellationToken);

            result.exists.Should().BeTrue();
            result.target.Should().Be(target);
        }

        [TestMethod]
        public async Task GetAsync_ReturnsFromDistributedCache_IfNotInMemory()
        {
            var sessionId = "s2";
            var target = "http://target";
            var cancellationToken = CancellationToken.None;
            var sessionInfo = new SessionInfo(target, DateTime.UtcNow);
            var json = JsonSerializer.Serialize(sessionInfo);

            _distributedCacheMock
                .Setup(x => x.GetAsync(sessionId, cancellationToken))
                .ReturnsAsync(Encoding.UTF8.GetBytes(json));

            var result = await _sessionStore.TryGetAsync(sessionId, cancellationToken);

            result.exists.Should().BeTrue();
            result.target.Should().Be(target);
        }

        [TestMethod]
        public async Task GetAsync_ReturnsFalse_IfNotInAnyCache()
        {
            var sessionId = "s3";
            var cancellationToken = CancellationToken.None;

            _distributedCacheMock
                .Setup(x => x.GetAsync(sessionId, cancellationToken))
                .Returns(Task.FromResult<byte[]?>(null));

            var (target, exists) = await _sessionStore.TryGetAsync(sessionId, cancellationToken);

            exists.Should().BeFalse();
            target.Should().BeNull();
        }

        [TestMethod]
        public async Task SetAsync_StoresInBothCaches()
        {
            var sessionId = "s4";
            var target = "http://target";
            var cancellationToken = CancellationToken.None;

            await _sessionStore.SetAsync(sessionId, target, cancellationToken);

            var result = await _sessionStore.TryGetAsync(sessionId, cancellationToken);

            result.exists.Should().BeTrue();
            result.target.Should().Be(target);
        }

        [TestMethod]
        public async Task RemoveAsync_RemovesFromBothCaches()
        {
            var sessionId = "s5";
            var target = "http://target";
            var cancellationToken = CancellationToken.None;

            await _sessionStore.SetAsync(sessionId, target, cancellationToken);
            await _sessionStore.RemoveAsync(sessionId, cancellationToken);

            _distributedCacheMock
                .Verify(x => x.RemoveAsync(sessionId, cancellationToken), Times.Once);

            var result = await _sessionStore.TryGetAsync(sessionId, cancellationToken);

            result.exists.Should().BeFalse();
            result.target.Should().BeNull();
        }
    }
}
