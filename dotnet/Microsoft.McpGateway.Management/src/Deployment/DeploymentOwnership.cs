using System.Net;
using k8s.Autorest;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.McpGateway.Management.Contracts;

namespace Microsoft.McpGateway.Management.Deployment
{
    /// <summary>
    /// Thrown when workload ownership cannot be determined because the Kubernetes API is unavailable; callers should retry.
    /// </summary>
    public sealed class DeploymentOwnershipUnavailableException(string message, Exception innerException) : Exception(message, innerException);

    /// <summary>
    /// Thrown when a create collides with a same-named workload that is still being deleted; callers should retry.
    /// </summary>
    public sealed class DeploymentDeletionPendingException(string message) : InvalidOperationException(message);

    /// <summary>
    /// Verifies that the StatefulSet and Service behind a resource name belong to that resource, caching results briefly.
    /// </summary>
    public class DeploymentOwnership(IKubeClientWrapper kubeClient, IOptions<GatewayKubernetesOptions>? options = null, TimeProvider? timeProvider = null) : IDisposable
    {
        private static readonly TimeSpan OwnedTtl = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan NotOwnedTtl = TimeSpan.FromSeconds(5);
        private readonly IKubeClientWrapper _kubeClient = kubeClient ?? throw new ArgumentNullException(nameof(kubeClient));
        private readonly string _namespace = options?.Value.Namespace ?? "adapter";
        private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
        private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 4096 });
        private readonly object _cacheLock = new();

        private sealed record OwnershipResult(bool Owned, string? StatefulSetUid, string? ServiceUid);

        private sealed class CacheEntry(Func<Task<OwnershipResult>> lookup, DateTimeOffset createdAt)
        {
            public Lazy<Task<OwnershipResult>> Lookup { get; } = new(lookup);
            public DateTimeOffset CreatedAt { get; } = createdAt;
            public bool Invalidated { get; set; }
        }

        public static bool Matches(IDictionary<string, string>? labels, string name, ResourceType resourceType) =>
            labels != null &&
            labels.TryGetValue("adapter/type", out var type) && type == resourceType.ToString().ToLowerInvariant() &&
            labels.TryGetValue("adapter/name", out var resourceName) && resourceName == name;

        public async Task VerifyAsync(string name, ResourceType resourceType, CancellationToken cancellationToken)
        {
            var key = (resourceType, name);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CacheEntry entry;
                lock (_cacheLock)
                {
                    if (!_cache.TryGetValue(key, out CacheEntry? cached) || cached == null || IsExpired(cached))
                    {
                        entry = new CacheEntry(() => ReadOwnershipAsync(name, resourceType), _time.GetUtcNow());
                        _cache.Set(key, entry, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = OwnedTtl });
                    }
                    else
                    {
                        entry = cached;
                    }
                }

                OwnershipResult result;
                try
                {
                    result = await entry.Lookup.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch when (!cancellationToken.IsCancellationRequested)
                {
                    lock (_cacheLock)
                    {
                        if (_cache.TryGetValue(key, out CacheEntry? current) && ReferenceEquals(current, entry))
                            _cache.Remove(key);
                    }
                    throw;
                }

                lock (_cacheLock)
                {
                    if (entry.Invalidated)
                        continue;
                }

                if (!result.Owned)
                    throw new UnauthorizedAccessException("The deployment does not belong to this resource.");
                return;
            }
        }

        public void Invalidate(string name)
        {
            lock (_cacheLock)
            {
                foreach (var resourceType in new[] { ResourceType.Mcp, ResourceType.Tool })
                {
                    var key = (resourceType, name);
                    if (_cache.TryGetValue(key, out CacheEntry? entry) && entry != null)
                        entry.Invalidated = true;
                    _cache.Remove(key);
                }
            }
        }

        private bool IsExpired(CacheEntry entry)
        {
            var ttl = entry.Lookup.IsValueCreated && entry.Lookup.Value.IsCompletedSuccessfully && !entry.Lookup.Value.Result.Owned
                ? NotOwnedTtl : OwnedTtl;
            return entry.Invalidated || _time.GetUtcNow() - entry.CreatedAt >= ttl;
        }

        private async Task<OwnershipResult> ReadOwnershipAsync(string name, ResourceType resourceType)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5), _time);
            var cancellationToken = timeout.Token;
            try
            {
                var statefulSetTask = IgnoreNotFoundAsync(() => _kubeClient.ReadStatefulSetAsync(name, _namespace, cancellationToken));
                var serviceTask = IgnoreNotFoundAsync(() => _kubeClient.ReadServiceAsync($"{name}-service", _namespace, cancellationToken));
                await Task.WhenAll(statefulSetTask, serviceTask).WaitAsync(cancellationToken).ConfigureAwait(false);
                var statefulSet = statefulSetTask.Result;
                var service = serviceTask.Result;

                var owned = statefulSet != null && statefulSet.Metadata?.DeletionTimestamp == null &&
                    !string.IsNullOrEmpty(statefulSet.Metadata?.Uid) &&
                    Matches(statefulSet.Spec?.Selector?.MatchLabels, name, resourceType) &&
                    service != null && service.Metadata?.DeletionTimestamp == null &&
                    !string.IsNullOrEmpty(service.Metadata?.Uid) &&
                    Matches(service.Spec?.Selector, name, resourceType);
                return new OwnershipResult(owned, statefulSet?.Metadata?.Uid, service?.Metadata?.Uid);
            }
            catch (Exception ex) when (ex is HttpOperationException or HttpRequestException or OperationCanceledException or TimeoutException)
            {
                throw new DeploymentOwnershipUnavailableException("Deployment ownership could not be verified. Retry later.", ex);
            }
        }

        private static async Task<T?> IgnoreNotFoundAsync<T>(Func<Task<T>> operation) where T : class
        {
            try
            {
                return await operation().ConfigureAwait(false);
            }
            catch (HttpOperationException ex) when (ex.Response?.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }
        }

        public void Dispose() => _cache.Dispose();
    }
}
