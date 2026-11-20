// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.McpGateway.Management.Contracts;
using Microsoft.McpGateway.Management.Store;
using ModelContextProtocol.Protocol;

namespace Microsoft.McpGateway.Management.Tests
{
    [TestClass]
    public class RedisResourceStoreLoggingTests
    {
        [DataTestMethod]
        [DataRow("safe-name", "safe-name")]
        [DataRow("a\r\nb\nc\rd\te", "abcde")]
        public async Task ToolStore_SanitizesNamesInLogsWithoutChangingStorage(string name, string sanitized)
        {
            var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
            var logger = new RecordingLogger<RedisToolResourceStore>();
            var store = new RedisToolResourceStore(cache, logger);

            (await store.TryGetAsync(name, CancellationToken.None)).Should().BeNull();
            var tool = new ToolResource
            {
                Id = name,
                Name = name,
                CreatedBy = "user",
                ImageName = "image",
                ImageVersion = "v1",
                ToolDefinition = new ToolDefinition
                {
                    Tool = new Tool { Name = name, InputSchema = JsonSerializer.SerializeToElement(new { type = "object" }) }
                }
            };
            await store.UpsertAsync(tool, CancellationToken.None);
            (await store.TryGetAsync(name, CancellationToken.None))!.Name.Should().Be(name);
            await store.DeleteAsync(name, CancellationToken.None);
            (await cache.GetStringAsync($"tool:{name}")).Should().BeNull();

            logger.Messages.Should().Equal(
                $"Tool {sanitized} not found in Redis",
                $"Upserted tool {sanitized} to Redis",
                $"Deleted tool {sanitized} from Redis");
        }

        [DataTestMethod]
        [DataRow("safe-name", "safe-name")]
        [DataRow("a\r\nb\nc\rd\te", "abcde")]
        public async Task AdapterStore_SanitizesNamesInLogsWithoutChangingStorage(string name, string sanitized)
        {
            var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
            var logger = new RecordingLogger<RedisAdapterResourceStore>();
            var store = new RedisAdapterResourceStore(cache, logger);

            (await store.TryGetAsync(name, CancellationToken.None)).Should().BeNull();
            var adapter = new AdapterResource
            {
                Id = name,
                Name = name,
                CreatedBy = "user",
                ImageName = "image",
                ImageVersion = "v1"
            };
            await store.UpsertAsync(adapter, CancellationToken.None);
            (await store.TryGetAsync(name, CancellationToken.None))!.Name.Should().Be(name);
            await store.DeleteAsync(name, CancellationToken.None);
            (await cache.GetStringAsync($"adapter:{name}")).Should().BeNull();

            logger.Messages.Should().Equal(
                $"Adapter {sanitized} not found in Redis",
                $"Upserted adapter {sanitized} to Redis",
                $"Deleted adapter {sanitized} from Redis");
        }

        private sealed class RecordingLogger<T> : ILogger<T>
        {
            public List<string> Messages { get; } = [];

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => Messages.Add(formatter(state, exception));
        }
    }
}
