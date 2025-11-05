// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.McpGateway.Management.Contracts;

namespace Microsoft.McpGateway.Management.Store
{
    /// <summary>
    /// Redis-backed implementation of IToolResourceStore for local development.
    /// Uses IDistributedCache (Redis) to share data across multiple service instances.
    /// </summary>
    public class RedisToolResourceStore : IToolResourceStore
    {
        private readonly IDistributedCache _cache;
        private readonly ILogger<RedisToolResourceStore> _logger;
        private const string KeyPrefix = "tool:";
        private const string ListKey = "tool:list";

        public RedisToolResourceStore(IDistributedCache cache, ILogger<RedisToolResourceStore> logger)
        {
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<ToolResource?> TryGetAsync(string name, CancellationToken cancellationToken)
        {
            var key = GetKey(name);
            var json = await _cache.GetStringAsync(key, cancellationToken).ConfigureAwait(false);
            
            if (string.IsNullOrEmpty(json))
            {
                _logger.LogDebug("Tool {Name} not found in Redis", name);
                return null;
            }

            return JsonSerializer.Deserialize<ToolResource>(json);
        }

        public async Task UpsertAsync(ToolResource tool, CancellationToken cancellationToken)
        {
            var key = GetKey(tool.Name);
            var json = JsonSerializer.Serialize(tool);
            
            await _cache.SetStringAsync(key, json, cancellationToken).ConfigureAwait(false);
            
            // Add to list for ListAsync
            await AddToListAsync(tool.Name, cancellationToken).ConfigureAwait(false);
            
            _logger.LogInformation("Upserted tool {Name} to Redis", tool.Name);
        }

        public async Task DeleteAsync(string name, CancellationToken cancellationToken)
        {
            var key = GetKey(name);
            await _cache.RemoveAsync(key, cancellationToken).ConfigureAwait(false);
            
            // Remove from list
            await RemoveFromListAsync(name, cancellationToken).ConfigureAwait(false);
            
            _logger.LogInformation("Deleted tool {Name} from Redis", name);
        }

        public async Task<IEnumerable<ToolResource>> ListAsync(CancellationToken cancellationToken)
        {
            var listJson = await _cache.GetStringAsync(ListKey, cancellationToken).ConfigureAwait(false);
            
            if (string.IsNullOrEmpty(listJson))
            {
                return Enumerable.Empty<ToolResource>();
            }

            var names = JsonSerializer.Deserialize<List<string>>(listJson) ?? new List<string>();
            var tools = new List<ToolResource>();

            foreach (var name in names)
            {
                var tool = await TryGetAsync(name, cancellationToken).ConfigureAwait(false);
                if (tool != null)
                {
                    tools.Add(tool);
                }
            }

            return tools;
        }

        private async Task AddToListAsync(string name, CancellationToken cancellationToken)
        {
            var listJson = await _cache.GetStringAsync(ListKey, cancellationToken).ConfigureAwait(false);
            var names = string.IsNullOrEmpty(listJson) 
                ? new HashSet<string>() 
                : JsonSerializer.Deserialize<HashSet<string>>(listJson) ?? new HashSet<string>();

            names.Add(name);
            
            var updatedJson = JsonSerializer.Serialize(names);
            await _cache.SetStringAsync(ListKey, updatedJson, cancellationToken).ConfigureAwait(false);
        }

        private async Task RemoveFromListAsync(string name, CancellationToken cancellationToken)
        {
            var listJson = await _cache.GetStringAsync(ListKey, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(listJson))
            {
                return;
            }

            var names = JsonSerializer.Deserialize<HashSet<string>>(listJson) ?? new HashSet<string>();
            names.Remove(name);
            
            var updatedJson = JsonSerializer.Serialize(names);
            await _cache.SetStringAsync(ListKey, updatedJson, cancellationToken).ConfigureAwait(false);
        }

        private static string GetKey(string name) => $"{KeyPrefix}{name}";
    }
}
