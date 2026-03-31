// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.McpGateway.Management.Contracts;
using Microsoft.McpGateway.Management.Store;
using ModelContextProtocol.Protocol;
using Moq;

namespace Microsoft.McpGateway.Management.Tests
{
    [TestClass]
    public class DistributedToolResourceStoreTests
    {
        private readonly Mock<IDistributedCache> _cacheMock;
        private readonly Mock<ILogger<DistributedToolResourceStore>> _loggerMock;
        private readonly DistributedToolResourceStore _store;

        public DistributedToolResourceStoreTests()
        {
            _cacheMock = new Mock<IDistributedCache>();
            _loggerMock = new Mock<ILogger<DistributedToolResourceStore>>();
            _store = new DistributedToolResourceStore(_cacheMock.Object, _loggerMock.Object);
        }

        private static ToolResource CreateTool(string name = "test-tool")
        {
            var inputSchemaJson = JsonSerializer.Serialize(new { type = "object", properties = new { } });
            var inputSchema = JsonSerializer.Deserialize<JsonElement>(inputSchemaJson);

            var toolData = new ToolData
            {
                Name = name,
                ImageName = "test-image",
                ImageVersion = "v1",
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

            return ToolResource.Create(toolData, "user1", DateTimeOffset.UtcNow);
        }

        [TestMethod]
        public async Task TryGetAsync_ShouldReturnTool_WhenFoundInCache()
        {
            var tool = CreateTool();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(tool);
            _cacheMock.Setup(x => x.GetAsync("tool:test-tool", It.IsAny<CancellationToken>()))
                .ReturnsAsync(bytes);

            var result = await _store.TryGetAsync("test-tool", CancellationToken.None);

            result.Should().NotBeNull();
            result!.Name.Should().Be("test-tool");
        }

        [TestMethod]
        public async Task TryGetAsync_ShouldReturnNull_WhenNotFoundInCache()
        {
            _cacheMock.Setup(x => x.GetAsync("tool:nonexistent", It.IsAny<CancellationToken>()))
                .ReturnsAsync((byte[]?)null);

            var result = await _store.TryGetAsync("nonexistent", CancellationToken.None);

            result.Should().BeNull();
        }

        [TestMethod]
        public async Task TryGetAsync_ShouldReturnNull_WhenCacheReturnsEmptyArray()
        {
            _cacheMock.Setup(x => x.GetAsync("tool:empty", It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<byte>());

            var result = await _store.TryGetAsync("empty", CancellationToken.None);

            result.Should().BeNull();
        }

        [TestMethod]
        public async Task UpsertAsync_ShouldSetToolAndUpdateList()
        {
            var tool = CreateTool();
            _cacheMock.Setup(x => x.GetAsync("tool:list", It.IsAny<CancellationToken>()))
                .ReturnsAsync((byte[]?)null);

            await _store.UpsertAsync(tool, CancellationToken.None);

            _cacheMock.Verify(x => x.SetAsync(
                "tool:test-tool",
                It.IsAny<byte[]>(),
                It.IsAny<DistributedCacheEntryOptions>(),
                It.IsAny<CancellationToken>()), Times.Once);

            _cacheMock.Verify(x => x.SetAsync(
                "tool:list",
                It.IsAny<byte[]>(),
                It.IsAny<DistributedCacheEntryOptions>(),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task UpsertAsync_ShouldNotDuplicateNameInList()
        {
            var tool = CreateTool();
            var existingList = JsonSerializer.SerializeToUtf8Bytes(new HashSet<string> { "test-tool" });
            _cacheMock.Setup(x => x.GetAsync("tool:list", It.IsAny<CancellationToken>()))
                .ReturnsAsync(existingList);

            byte[]? savedListBytes = null;
            _cacheMock.Setup(x => x.SetAsync("tool:list", It.IsAny<byte[]>(), It.IsAny<DistributedCacheEntryOptions>(), It.IsAny<CancellationToken>()))
                .Callback<string, byte[], DistributedCacheEntryOptions, CancellationToken>((_, bytes, _, _) => savedListBytes = bytes)
                .Returns(Task.CompletedTask);

            await _store.UpsertAsync(tool, CancellationToken.None);

            savedListBytes.Should().NotBeNull();
            var savedNames = JsonSerializer.Deserialize<List<string>>(savedListBytes!);
            savedNames.Should().HaveCount(1);
            savedNames.Should().Contain("test-tool");
        }

        [TestMethod]
        public async Task DeleteAsync_ShouldRemoveToolAndUpdateList()
        {
            var existingList = JsonSerializer.SerializeToUtf8Bytes(new HashSet<string> { "test-tool", "other-tool" });
            _cacheMock.Setup(x => x.GetAsync("tool:list", It.IsAny<CancellationToken>()))
                .ReturnsAsync(existingList);

            byte[]? savedListBytes = null;
            _cacheMock.Setup(x => x.SetAsync("tool:list", It.IsAny<byte[]>(), It.IsAny<DistributedCacheEntryOptions>(), It.IsAny<CancellationToken>()))
                .Callback<string, byte[], DistributedCacheEntryOptions, CancellationToken>((_, bytes, _, _) => savedListBytes = bytes)
                .Returns(Task.CompletedTask);

            await _store.DeleteAsync("test-tool", CancellationToken.None);

            _cacheMock.Verify(x => x.RemoveAsync("tool:test-tool", It.IsAny<CancellationToken>()), Times.Once);

            savedListBytes.Should().NotBeNull();
            var savedNames = JsonSerializer.Deserialize<List<string>>(savedListBytes!);
            savedNames.Should().NotContain("test-tool");
            savedNames.Should().Contain("other-tool");
        }

        [TestMethod]
        public async Task DeleteAsync_ShouldHandleEmptyList()
        {
            _cacheMock.Setup(x => x.GetAsync("tool:list", It.IsAny<CancellationToken>()))
                .ReturnsAsync((byte[]?)null);

            await _store.DeleteAsync("test-tool", CancellationToken.None);

            _cacheMock.Verify(x => x.RemoveAsync("tool:test-tool", It.IsAny<CancellationToken>()), Times.Once);
            _cacheMock.Verify(x => x.SetAsync("tool:list", It.IsAny<byte[]>(), It.IsAny<DistributedCacheEntryOptions>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [TestMethod]
        public async Task ListAsync_ShouldReturnAllTools()
        {
            var tool1 = CreateTool("tool-1");
            var tool2 = CreateTool("tool-2");

            var listBytes = JsonSerializer.SerializeToUtf8Bytes(new List<string> { "tool-1", "tool-2" });
            _cacheMock.Setup(x => x.GetAsync("tool:list", It.IsAny<CancellationToken>()))
                .ReturnsAsync(listBytes);
            _cacheMock.Setup(x => x.GetAsync("tool:tool-1", It.IsAny<CancellationToken>()))
                .ReturnsAsync(JsonSerializer.SerializeToUtf8Bytes(tool1));
            _cacheMock.Setup(x => x.GetAsync("tool:tool-2", It.IsAny<CancellationToken>()))
                .ReturnsAsync(JsonSerializer.SerializeToUtf8Bytes(tool2));

            var result = (await _store.ListAsync(CancellationToken.None)).ToList();

            result.Should().HaveCount(2);
            result.Select(t => t.Name).Should().BeEquivalentTo(["tool-1", "tool-2"]);
        }

        [TestMethod]
        public async Task ListAsync_ShouldReturnEmpty_WhenNoListExists()
        {
            _cacheMock.Setup(x => x.GetAsync("tool:list", It.IsAny<CancellationToken>()))
                .ReturnsAsync((byte[]?)null);

            var result = await _store.ListAsync(CancellationToken.None);

            result.Should().BeEmpty();
        }

        [TestMethod]
        public async Task ListAsync_ShouldSkipMissingTools()
        {
            var tool1 = CreateTool("tool-1");

            var listBytes = JsonSerializer.SerializeToUtf8Bytes(new List<string> { "tool-1", "tool-missing" });
            _cacheMock.Setup(x => x.GetAsync("tool:list", It.IsAny<CancellationToken>()))
                .ReturnsAsync(listBytes);
            _cacheMock.Setup(x => x.GetAsync("tool:tool-1", It.IsAny<CancellationToken>()))
                .ReturnsAsync(JsonSerializer.SerializeToUtf8Bytes(tool1));
            _cacheMock.Setup(x => x.GetAsync("tool:tool-missing", It.IsAny<CancellationToken>()))
                .ReturnsAsync((byte[]?)null);

            var result = (await _store.ListAsync(CancellationToken.None)).ToList();

            result.Should().HaveCount(1);
            result[0].Name.Should().Be("tool-1");
        }

        [TestMethod]
        public void Constructor_ShouldThrow_WhenCacheIsNull()
        {
            Action act = () => new DistributedToolResourceStore(null!, _loggerMock.Object);
            act.Should().Throw<ArgumentNullException>().WithParameterName("cache");
        }

        [TestMethod]
        public void Constructor_ShouldThrow_WhenLoggerIsNull()
        {
            Action act = () => new DistributedToolResourceStore(_cacheMock.Object, null!);
            act.Should().Throw<ArgumentNullException>().WithParameterName("logger");
        }
    }
}
