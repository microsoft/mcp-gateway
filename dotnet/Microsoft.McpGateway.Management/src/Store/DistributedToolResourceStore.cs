// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.McpGateway.Management.Contracts;

namespace Microsoft.McpGateway.Management.Store
{
    /// <summary>
    /// Distributed cache-backed implementation of IToolResourceStore.
    /// Uses IDistributedCache to share data across multiple service instances.
    /// </summary>
    public class DistributedToolResourceStore : IToolResourceStore
    {
        private readonly IDistributedCache _cache;
        private readonly DistributedCacheEntryOptions _cacheOptions = new();
        private readonly ILogger<DistributedToolResourceStore> _logger;

        private const string KeyPrefix = "tool:";
        private const string ListKey = "tool:list";

        private static string GetKey(string name) => $"{KeyPrefix}{name}";

        public DistributedToolResourceStore(IDistributedCache cache, ILogger<DistributedToolResourceStore> logger)
        {
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<ToolResource?> TryGetAsync(string name, CancellationToken cancellationToken)
        {
            var key = GetKey(name);
            var json = await _cache.GetAsync(key, cancellationToken).ConfigureAwait(false);
            
            if (json == null || json.Length == 0)
            {
                _logger.LogDebug("Tool {Name} not found in distributed cache", name);
                return null;
            }

            return JsonSerializer.Deserialize<ToolResource>(json);
        }


        public async Task UpsertAsync(ToolResource tool, CancellationToken cancellationToken)
        {
            var key = GetKey(tool.Name);
            var json = JsonSerializer.SerializeToUtf8Bytes(tool);
            
            await _cache.SetAsync(key, json, _cacheOptions, cancellationToken).ConfigureAwait(false);
            
            // Add to list for ListAsync
            await AddToListAsync(tool.Name, cancellationToken).ConfigureAwait(false);
            
            _logger.LogInformation("Upserted tool {Name} to distributed cache", tool.Name);
        }

        public async Task DeleteAsync(string name, CancellationToken cancellationToken)
        {
            var key = GetKey(name);
            await _cache.RemoveAsync(key, cancellationToken).ConfigureAwait(false);
            
            // Remove from list
            await RemoveFromListAsync(name, cancellationToken).ConfigureAwait(false);
            
            _logger.LogInformation("Deleted tool {Name} from distributed cache", name);
        }

        public async Task<IEnumerable<ToolResource>> ListAsync(CancellationToken cancellationToken)
        {
            var listJson = await _cache.GetAsync(ListKey, cancellationToken).ConfigureAwait(false);
            
            if (listJson == null || listJson.Length == 0)
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
            var listJson = await _cache.GetAsync(ListKey, cancellationToken).ConfigureAwait(false);
            var names = (listJson == null || listJson.Length == 0) 
                ? new HashSet<string>()
                : JsonSerializer.Deserialize<HashSet<string>>(listJson) ?? new HashSet<string>();

            names.Add(name);
            
            var updatedJson = JsonSerializer.SerializeToUtf8Bytes(names);
            await _cache.SetAsync(ListKey, updatedJson, _cacheOptions, cancellationToken).ConfigureAwait(false);
        }

        private async Task RemoveFromListAsync(string name, CancellationToken cancellationToken)
        {
            var listJson = await _cache.GetAsync(ListKey, cancellationToken).ConfigureAwait(false);
            if (listJson == null || listJson.Length == 0) 
            {
                return;
            }

            var names = JsonSerializer.Deserialize<HashSet<string>>(listJson) ?? new HashSet<string>();
            names.Remove(name);
            
            var updatedJson = JsonSerializer.SerializeToUtf8Bytes(names);
            await _cache.SetAsync(ListKey, updatedJson, _cacheOptions, cancellationToken).ConfigureAwait(false);
        }
    }
}
