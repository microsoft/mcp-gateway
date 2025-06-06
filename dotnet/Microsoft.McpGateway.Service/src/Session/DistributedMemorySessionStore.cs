// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;

namespace Microsoft.McpGateway.Service.Session
{
    /// <summary>
    /// Session Store with distributed and local cache implementation. This instance needs to be a singleton.
    /// </summary>
    public class DistributedMemorySessionStore : IAdapterSessionStore, IDisposable
    {
        private readonly IDistributedCache _distributedCache;
        private readonly ConcurrentDictionary<string, SessionInfo> _inMemoryCache;
        private readonly DistributedCacheEntryOptions _cacheEntryOptions;
        private readonly ILogger<DistributedMemorySessionStore> _logger;
        private readonly TimeSpan _inMemoryTtl;
        private readonly CancellationTokenSource _cancellationTokenSource = new();
        private bool _disposed = false;

        public DistributedMemorySessionStore(IDistributedCache distributedCache, ILogger<DistributedMemorySessionStore> logger, int slidingWindowExpirationHours = 1, int absoluteExpirationHours = 12)
        {
            if (slidingWindowExpirationHours <= 0 || absoluteExpirationHours <= 0)
                throw new ArgumentOutOfRangeException("Expiration hours must be positive.");

            _distributedCache = distributedCache ?? throw new ArgumentNullException(nameof(distributedCache));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _inMemoryCache = new ConcurrentDictionary<string, SessionInfo>();
            _inMemoryTtl = TimeSpan.FromHours(slidingWindowExpirationHours);
            _cacheEntryOptions = new() { AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(absoluteExpirationHours) };
            StartCleanupLoop();
        }

        public async Task<(string? target, bool exists)> TryGetAsync(string sessionId, CancellationToken cancellationToken)
        {
            // 1. Check in-memory cache
            if (_inMemoryCache.TryGetValue(sessionId, out var cached))
                return (cached.Target, true);

            // 2. Fallback to distributed cache
            var json = await _distributedCache.GetStringAsync(sessionId, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(json))
                return (null, false);

            var item = JsonSerializer.Deserialize<SessionInfo>(json);
            if (item == null)
                return (null, false);

            // Update in-memory cache
            var info = new SessionInfo(item.Target, DateTime.UtcNow);
            _inMemoryCache[sessionId] = info;

            return (item.Target, true);
        }

        public async Task SetAsync(string sessionId, string target, CancellationToken cancellationToken)
        {
            var now = DateTime.UtcNow;
            var item = new SessionInfo(target, now);

            await _distributedCache.SetStringAsync(sessionId, JsonSerializer.Serialize(item), _cacheEntryOptions, cancellationToken).ConfigureAwait(false);
            _inMemoryCache[sessionId] = item;
        }

        public async Task RemoveAsync(string sessionId, CancellationToken cancellationToken)
        {
            _inMemoryCache.TryRemove(sessionId, out _);
            await _distributedCache.RemoveAsync(sessionId, cancellationToken).ConfigureAwait(false);
        }

        private void StartCleanupLoop()
        {
            _ = Task.Run(async () =>
            {
                var token = _cancellationTokenSource.Token;
                while (!token.IsCancellationRequested)
                {
                    _logger.LogInformation("Start cleaning up in-memory session store cache.");
                    try
                    {
                        var now = DateTime.UtcNow;
                        foreach (var kvp in _inMemoryCache)
                        {
                            if (now - kvp.Value.LastUsed > _inMemoryTtl)
                                _inMemoryCache.TryRemove(kvp.Key, out _);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("Exception occurred when trying to clean up in-memory cache: {message}", ex.Message);
                    }

                    _logger.LogInformation("Finish cleaning up in-memory session store cache.");

                    await Task.Delay(TimeSpan.FromMinutes(10)).ConfigureAwait(false);
                }
            });
        }

        public void Dispose()
        {
            Dispose(true);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed && disposing)
            {
                _cancellationTokenSource.Cancel();
                _cancellationTokenSource.Dispose();
            }

            _disposed = true;
        }
    }
}
