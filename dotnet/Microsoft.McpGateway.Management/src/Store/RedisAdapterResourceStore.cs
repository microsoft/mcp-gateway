// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.McpGateway.Management.Contracts;

namespace Microsoft.McpGateway.Management.Store
{
    /// <summary>
    /// Redis-backed implementation of IAdapterResourceStore for local development.
    /// Uses IDistributedCache (Redis) to share data across multiple service instances.
    /// </summary>
    public class RedisAdapterResourceStore : IAdapterResourceStore
    {
        private readonly IDistributedCache _cache;
        private readonly ILogger<RedisAdapterResourceStore> _logger;
        private const string KeyPrefix = "adapter:";
        private const string ListKey = "adapter:list";

        public RedisAdapterResourceStore(IDistributedCache cache, ILogger<RedisAdapterResourceStore> logger)
        {
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<AdapterResource?> TryGetAsync(string name, CancellationToken cancellationToken)
        {
            var key = GetKey(name);
            var json = await _cache.GetStringAsync(key, cancellationToken).ConfigureAwait(false);
            
            if (string.IsNullOrEmpty(json))
            {
                _logger.LogDebug("Adapter {Name} not found in Redis", name);
                return null;
            }

            return JsonSerializer.Deserialize<AdapterResource>(json);
        }

        public async Task UpsertAsync(AdapterResource adapter, CancellationToken cancellationToken)
        {
            var key = GetKey(adapter.Name);
            var json = JsonSerializer.Serialize(adapter);
            
            await _cache.SetStringAsync(key, json, cancellationToken).ConfigureAwait(false);
            
            // Add to list for ListAsync
            await AddToListAsync(adapter.Name, cancellationToken).ConfigureAwait(false);
            
            _logger.LogInformation("Upserted adapter {Name} to Redis", adapter.Name);
        }

        public async Task DeleteAsync(string name, CancellationToken cancellationToken)
        {
            var key = GetKey(name);
            await _cache.RemoveAsync(key, cancellationToken).ConfigureAwait(false);
            
            // Remove from list
            await RemoveFromListAsync(name, cancellationToken).ConfigureAwait(false);
            
            _logger.LogInformation("Deleted adapter {Name} from Redis", name);
        }

        public async Task<IEnumerable<AdapterResource>> ListAsync(CancellationToken cancellationToken)
        {
            var listJson = await _cache.GetStringAsync(ListKey, cancellationToken).ConfigureAwait(false);
            
            if (string.IsNullOrEmpty(listJson))
            {
                return Enumerable.Empty<AdapterResource>();
            }

            var names = JsonSerializer.Deserialize<List<string>>(listJson) ?? new List<string>();
            var adapters = new List<AdapterResource>();

            foreach (var name in names)
            {
                var adapter = await TryGetAsync(name, cancellationToken).ConfigureAwait(false);
                if (adapter != null)
                {
                    adapters.Add(adapter);
                }
            }

            return adapters;
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
