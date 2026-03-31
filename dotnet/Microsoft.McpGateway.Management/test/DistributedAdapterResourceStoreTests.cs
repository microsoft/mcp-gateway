// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.McpGateway.Management.Contracts;
using Microsoft.McpGateway.Management.Store;
using Moq;

namespace Microsoft.McpGateway.Management.Tests
{
    [TestClass]
    public class DistributedAdapterResourceStoreTests
    {
        private readonly Mock<IDistributedCache> _cacheMock;
        private readonly Mock<ILogger<DistributedAdapterResourceStore>> _loggerMock;
        private readonly DistributedAdapterResourceStore _store;

        public DistributedAdapterResourceStoreTests()
        {
            _cacheMock = new Mock<IDistributedCache>();
            _loggerMock = new Mock<ILogger<DistributedAdapterResourceStore>>();
            _store = new DistributedAdapterResourceStore(_cacheMock.Object, _loggerMock.Object);
        }

        private static AdapterResource CreateAdapter(string name = "test-adapter") =>
            AdapterResource.Create(
                new AdapterData { Name = name, ImageName = "image", ImageVersion = "v1", ReplicaCount = 1, EnvironmentVariables = [] },
                "user1",
                DateTimeOffset.UtcNow);

        [TestMethod]
        public async Task TryGetAsync_ShouldReturnAdapter_WhenFoundInCache()
        {
            var adapter = CreateAdapter();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(adapter);
            _cacheMock.Setup(x => x.GetAsync("adapter:test-adapter", It.IsAny<CancellationToken>()))
                .ReturnsAsync(bytes);

            var result = await _store.TryGetAsync("test-adapter", CancellationToken.None);

            result.Should().NotBeNull();
            result!.Name.Should().Be("test-adapter");
        }

        [TestMethod]
        public async Task TryGetAsync_ShouldReturnNull_WhenNotFoundInCache()
        {
            _cacheMock.Setup(x => x.GetAsync("adapter:nonexistent", It.IsAny<CancellationToken>()))
                .ReturnsAsync((byte[]?)null);

            var result = await _store.TryGetAsync("nonexistent", CancellationToken.None);

            result.Should().BeNull();
        }

        [TestMethod]
        public async Task TryGetAsync_ShouldReturnNull_WhenCacheReturnsEmptyArray()
        {
            _cacheMock.Setup(x => x.GetAsync("adapter:empty", It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<byte>());

            var result = await _store.TryGetAsync("empty", CancellationToken.None);

            result.Should().BeNull();
        }

        [TestMethod]
        public async Task UpsertAsync_ShouldSetAdapterAndUpdateList()
        {
            var adapter = CreateAdapter();
            _cacheMock.Setup(x => x.GetAsync("adapter:list", It.IsAny<CancellationToken>()))
                .ReturnsAsync((byte[]?)null);

            await _store.UpsertAsync(adapter, CancellationToken.None);

            _cacheMock.Verify(x => x.SetAsync(
                "adapter:test-adapter",
                It.IsAny<byte[]>(),
                It.IsAny<DistributedCacheEntryOptions>(),
                It.IsAny<CancellationToken>()), Times.Once);

            _cacheMock.Verify(x => x.SetAsync(
                "adapter:list",
                It.IsAny<byte[]>(),
                It.IsAny<DistributedCacheEntryOptions>(),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task UpsertAsync_ShouldNotDuplicateNameInList()
        {
            var adapter = CreateAdapter();
            var existingList = JsonSerializer.SerializeToUtf8Bytes(new HashSet<string> { "test-adapter" });
            _cacheMock.Setup(x => x.GetAsync("adapter:list", It.IsAny<CancellationToken>()))
                .ReturnsAsync(existingList);

            byte[]? savedListBytes = null;
            _cacheMock.Setup(x => x.SetAsync("adapter:list", It.IsAny<byte[]>(), It.IsAny<DistributedCacheEntryOptions>(), It.IsAny<CancellationToken>()))
                .Callback<string, byte[], DistributedCacheEntryOptions, CancellationToken>((_, bytes, _, _) => savedListBytes = bytes)
                .Returns(Task.CompletedTask);

            await _store.UpsertAsync(adapter, CancellationToken.None);

            savedListBytes.Should().NotBeNull();
            var savedNames = JsonSerializer.Deserialize<List<string>>(savedListBytes!);
            savedNames.Should().HaveCount(1);
            savedNames.Should().Contain("test-adapter");
        }

        [TestMethod]
        public async Task DeleteAsync_ShouldRemoveAdapterAndUpdateList()
        {
            var existingList = JsonSerializer.SerializeToUtf8Bytes(new HashSet<string> { "test-adapter", "other-adapter" });
            _cacheMock.Setup(x => x.GetAsync("adapter:list", It.IsAny<CancellationToken>()))
                .ReturnsAsync(existingList);

            byte[]? savedListBytes = null;
            _cacheMock.Setup(x => x.SetAsync("adapter:list", It.IsAny<byte[]>(), It.IsAny<DistributedCacheEntryOptions>(), It.IsAny<CancellationToken>()))
                .Callback<string, byte[], DistributedCacheEntryOptions, CancellationToken>((_, bytes, _, _) => savedListBytes = bytes)
                .Returns(Task.CompletedTask);

            await _store.DeleteAsync("test-adapter", CancellationToken.None);

            _cacheMock.Verify(x => x.RemoveAsync("adapter:test-adapter", It.IsAny<CancellationToken>()), Times.Once);

            savedListBytes.Should().NotBeNull();
            var savedNames = JsonSerializer.Deserialize<List<string>>(savedListBytes!);
            savedNames.Should().NotContain("test-adapter");
            savedNames.Should().Contain("other-adapter");
        }

        [TestMethod]
        public async Task DeleteAsync_ShouldHandleEmptyList()
        {
            _cacheMock.Setup(x => x.GetAsync("adapter:list", It.IsAny<CancellationToken>()))
                .ReturnsAsync((byte[]?)null);

            await _store.DeleteAsync("test-adapter", CancellationToken.None);

            _cacheMock.Verify(x => x.RemoveAsync("adapter:test-adapter", It.IsAny<CancellationToken>()), Times.Once);
            _cacheMock.Verify(x => x.SetAsync("adapter:list", It.IsAny<byte[]>(), It.IsAny<DistributedCacheEntryOptions>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [TestMethod]
        public async Task ListAsync_ShouldReturnAllAdapters()
        {
            var adapter1 = CreateAdapter("adapter-1");
            var adapter2 = CreateAdapter("adapter-2");

            var listBytes = JsonSerializer.SerializeToUtf8Bytes(new List<string> { "adapter-1", "adapter-2" });
            _cacheMock.Setup(x => x.GetAsync("adapter:list", It.IsAny<CancellationToken>()))
                .ReturnsAsync(listBytes);
            _cacheMock.Setup(x => x.GetAsync("adapter:adapter-1", It.IsAny<CancellationToken>()))
                .ReturnsAsync(JsonSerializer.SerializeToUtf8Bytes(adapter1));
            _cacheMock.Setup(x => x.GetAsync("adapter:adapter-2", It.IsAny<CancellationToken>()))
                .ReturnsAsync(JsonSerializer.SerializeToUtf8Bytes(adapter2));

            var result = (await _store.ListAsync(CancellationToken.None)).ToList();

            result.Should().HaveCount(2);
            result.Select(a => a.Name).Should().BeEquivalentTo(["adapter-1", "adapter-2"]);
        }

        [TestMethod]
        public async Task ListAsync_ShouldReturnEmpty_WhenNoListExists()
        {
            _cacheMock.Setup(x => x.GetAsync("adapter:list", It.IsAny<CancellationToken>()))
                .ReturnsAsync((byte[]?)null);

            var result = await _store.ListAsync(CancellationToken.None);

            result.Should().BeEmpty();
        }

        [TestMethod]
        public async Task ListAsync_ShouldSkipMissingAdapters()
        {
            var adapter1 = CreateAdapter("adapter-1");

            var listBytes = JsonSerializer.SerializeToUtf8Bytes(new List<string> { "adapter-1", "adapter-missing" });
            _cacheMock.Setup(x => x.GetAsync("adapter:list", It.IsAny<CancellationToken>()))
                .ReturnsAsync(listBytes);
            _cacheMock.Setup(x => x.GetAsync("adapter:adapter-1", It.IsAny<CancellationToken>()))
                .ReturnsAsync(JsonSerializer.SerializeToUtf8Bytes(adapter1));
            _cacheMock.Setup(x => x.GetAsync("adapter:adapter-missing", It.IsAny<CancellationToken>()))
                .ReturnsAsync((byte[]?)null);

            var result = (await _store.ListAsync(CancellationToken.None)).ToList();

            result.Should().HaveCount(1);
            result[0].Name.Should().Be("adapter-1");
        }

        [TestMethod]
        public void Constructor_ShouldThrow_WhenCacheIsNull()
        {
            Action act = () => new DistributedAdapterResourceStore(null!, _loggerMock.Object);
            act.Should().Throw<ArgumentNullException>().WithParameterName("cache");
        }

        [TestMethod]
        public void Constructor_ShouldThrow_WhenLoggerIsNull()
        {
            Action act = () => new DistributedAdapterResourceStore(_cacheMock.Object, null!);
            act.Should().Throw<ArgumentNullException>().WithParameterName("logger");
        }
    }
}
