// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.McpGateway.Management.Contracts;

namespace Microsoft.McpGateway.Management.Store
{
    /// <summary>
    /// Distributed cache-backed implementation of IAdapterResourceStore.
    /// Uses IDistributedCache to share data across multiple service instances.
    /// </summary>
    public class DistributedAdapterResourceStore : IAdapterResourceStore
    {
        private readonly IDistributedCache _cache;
        private readonly DistributedCacheEntryOptions _cacheOptions = new();
        private readonly ILogger<DistributedAdapterResourceStore> _logger;

        private const string KeyPrefix = "adapter:";
        private const string ListKey = "adapter:list";

        private static string GetKey(string name) => $"{KeyPrefix}{name}";

        public DistributedAdapterResourceStore(IDistributedCache cache, ILogger<DistributedAdapterResourceStore> logger)
        {
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<AdapterResource?> TryGetAsync(string name, CancellationToken cancellationToken)
        {
            var key = GetKey(name);
            var json = await _cache.GetAsync(key, cancellationToken).ConfigureAwait(false);

            if (json == null || json.Length == 0)
            {
                _logger.LogDebug("Adapter {Name} not found in distributed cache", name);
                return null;
            }

            return JsonSerializer.Deserialize<AdapterResource>(json);
        }

        public async Task UpsertAsync(AdapterResource adapter, CancellationToken cancellationToken)
        {
            var key = GetKey(adapter.Name);
            var json = JsonSerializer.SerializeToUtf8Bytes(adapter);

            await _cache.SetAsync(key, json, _cacheOptions, cancellationToken).ConfigureAwait(false);

            // Add to list for ListAsync
            await AddToListAsync(adapter.Name, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Upserted adapter {Name} to distributed cache", adapter.Name);
        }

        public async Task DeleteAsync(string name, CancellationToken cancellationToken)
        {
            var key = GetKey(name);
            await _cache.RemoveAsync(key, cancellationToken).ConfigureAwait(false);

            // Remove from list
            await RemoveFromListAsync(name, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Deleted adapter {Name} from distributed cache", name);
        }

        public async Task<IEnumerable<AdapterResource>> ListAsync(CancellationToken cancellationToken)
        {
            var listJson = await _cache.GetAsync(ListKey, cancellationToken).ConfigureAwait(false);

            if (listJson == null || listJson.Length == 0)
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
