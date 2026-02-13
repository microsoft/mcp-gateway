// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Azure.ResourceManager.AppService;
using Microsoft.Extensions.Caching.Distributed;

namespace Microsoft.McpGateway.Service.AppService;

/// <summary>
/// Allocates and tracks ports for sitecontainers.
/// Ports 8001-8009 are available for MCP server sidecars.
/// </summary>
public class SiteContainerPortAllocator
{
    private const int BasePort = 8001;
    private const int MaxPort = 8009;  // Max 9 sidecars in App Service

    private readonly IDistributedCache _cache;
    private readonly ILogger<SiteContainerPortAllocator> _logger;

    public SiteContainerPortAllocator(
        IDistributedCache cache,
        ILogger<SiteContainerPortAllocator> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    /// <summary>
    /// Allocates the next available port for a new adapter.
    /// </summary>
    public async Task<int> AllocatePortAsync(
        string adapterName,
        SiteContainerCollection existingContainers,
        CancellationToken cancellationToken)
    {
        var usedPorts = new HashSet<int>();

        // Get all used ports from existing containers
        await foreach (var container in existingContainers.GetAllAsync(cancellationToken))
        {
            if (container.Data.IsMain == true)
                continue;

            if (int.TryParse(container.Data.TargetPort, out var port))
            {
                usedPorts.Add(port);
                _logger.LogDebug("Port {port} is in use by container {name}", port, container.Data.Name);
            }
        }

        // Find first available port
        for (int port = BasePort; port <= MaxPort; port++)
        {
            if (!usedPorts.Contains(port))
            {
                _logger.LogInformation("Allocated port {port} for adapter {name}", port, adapterName);
                return port;
            }
        }

        throw new InvalidOperationException(
            $"No available ports. Maximum {MaxPort - BasePort + 1} sidecar containers supported in App Service.");
    }

    /// <summary>
    /// Saves the port mapping to cache for fast lookups during routing.
    /// </summary>
    public async Task SavePortMappingAsync(string adapterName, int port, CancellationToken cancellationToken)
    {
        var cacheKey = GetCacheKey(adapterName);
        await _cache.SetStringAsync(
            cacheKey,
            port.ToString(),
            new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(24)
            },
            cancellationToken);

        _logger.LogDebug("Saved port mapping: {name} -> {port}", adapterName, port);
    }

    /// <summary>
    /// Gets the cached port for an adapter.
    /// </summary>
    public async Task<int?> GetPortAsync(string adapterName, CancellationToken cancellationToken)
    {
        var cacheKey = GetCacheKey(adapterName);
        var portStr = await _cache.GetStringAsync(cacheKey, cancellationToken);

        if (int.TryParse(portStr, out var port))
        {
            return port;
        }

        return null;
    }

    /// <summary>
    /// Releases the port mapping when an adapter is deleted.
    /// </summary>
    public async Task ReleasePortAsync(string adapterName, CancellationToken cancellationToken)
    {
        var cacheKey = GetCacheKey(adapterName);
        await _cache.RemoveAsync(cacheKey, cancellationToken);
        _logger.LogDebug("Released port mapping for adapter: {name}", adapterName);
    }

    private static string GetCacheKey(string adapterName) => $"adapter:port:{adapterName}";
}
