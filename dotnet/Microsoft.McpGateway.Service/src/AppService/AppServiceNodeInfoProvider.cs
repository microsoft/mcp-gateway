// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using Azure;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.AppService;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.McpGateway.Service.Routing;

namespace Microsoft.McpGateway.Service.AppService;

/// <summary>
/// Provides information about MCP server addresses running as App Service sitecontainers.
/// In App Service, all containers share localhost, so routing uses localhost:{port}.
/// </summary>
public class AppServiceNodeInfoProvider : IServiceNodeInfoProvider
{
    private readonly ArmClient _armClient;
    private readonly string _subscriptionId;
    private readonly string _resourceGroupName;
    private readonly string _webAppName;
    private readonly SiteContainerPortAllocator _portAllocator;
    private readonly ILogger<AppServiceNodeInfoProvider> _logger;
    private readonly ConcurrentDictionary<string, string> _containerAddresses = new();
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly TaskCompletionSource<bool> _initialFetchCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;

    public AppServiceNodeInfoProvider(
        SiteContainerPortAllocator portAllocator,
        ILogger<AppServiceNodeInfoProvider> logger)
    {
        _portAllocator = portAllocator;
        _logger = logger;

        _armClient = new ArmClient(new DefaultAzureCredential());

        _subscriptionId = GetRequiredEnvVar("WEBSITE_OWNER_NAME").Split('+')[0];
        _resourceGroupName = GetRequiredEnvVar("WEBSITE_RESOURCE_GROUP");
        _webAppName = GetRequiredEnvVar("WEBSITE_SITE_NAME");

        _logger.LogInformation(
            "AppServiceNodeInfoProvider initialized for {subscription}/{resourceGroup}/{webApp}",
            _subscriptionId, _resourceGroupName, _webAppName);

        // Start background task to refresh container info
        StartContainerDiscovery();
    }

    public async Task<IDictionary<string, string>> GetNodeAddressesAsync(string serviceName, CancellationToken cancellationToken)
    {
        // Wait for initial fetch to complete
        await _initialFetchCompleted.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

        // First try to get from cached port mapping (fast path)
        var port = await _portAllocator.GetPortAsync(serviceName, cancellationToken);
        if (port.HasValue)
        {
            // In App Service, all containers share localhost
            // Return a single "node" with localhost address
            return new Dictionary<string, string>
            {
                { serviceName, $"http://localhost:{port}" }
            };
        }

        // Fall back to check in-memory cache from discovery
        if (_containerAddresses.TryGetValue(serviceName, out var address))
        {
            return new Dictionary<string, string>
            {
                { serviceName, address }
            };
        }

        _logger.LogWarning("No address found for service {serviceName}", serviceName);
        return new Dictionary<string, string>();
    }

    private void StartContainerDiscovery()
    {
        _ = Task.Run(async () =>
        {
            _logger.LogInformation("Starting sitecontainer discovery");

            var cancellationToken = _cancellationTokenSource.Token;
            var retryCount = 0;
            const int maxRetries = 5;
            const int baseDelaySeconds = 10;

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await RefreshContainerAddressesAsync(cancellationToken);
                    retryCount = 0;  // Reset on success

                    if (!_initialFetchCompleted.Task.IsCompleted)
                    {
                        _initialFetchCompleted.TrySetResult(true);
                    }

                    // Refresh every 30 seconds (ARM API doesn't support watch like K8s)
                    await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    retryCount++;
                    var delay = Math.Min(baseDelaySeconds * Math.Pow(2, retryCount), 300); // Max 5 min

                    _logger.LogWarning(
                        "Failed to refresh sitecontainer addresses (attempt {attempt}/{max}): {message}. Retrying in {delay}s",
                        retryCount, maxRetries, ex.Message, delay);

                    // Complete initial fetch even on error to unblock waiting requests
                    if (!_initialFetchCompleted.Task.IsCompleted && retryCount >= maxRetries)
                    {
                        _initialFetchCompleted.TrySetResult(true);
                    }

                    await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken);
                }
            }
        });
    }

    private async Task RefreshContainerAddressesAsync(CancellationToken cancellationToken)
    {
        var resourceId = WebSiteResource.CreateResourceIdentifier(
            _subscriptionId, _resourceGroupName, _webAppName);
        var webApp = await _armClient.GetWebSiteResource(resourceId).GetAsync(cancellationToken);
        var containers = webApp.Value.GetSiteContainers();

        var discoveredCount = 0;
        await foreach (var container in containers.GetAllAsync(cancellationToken))
        {
            // Skip main container
            if (container.Data.IsMain == true)
                continue;

            var name = container.Data.Name;
            if (int.TryParse(container.Data.TargetPort, out var port))
            {
                var address = $"http://localhost:{port}";
                _containerAddresses[name] = address;

                // Also save to port allocator cache for consistency
                await _portAllocator.SavePortMappingAsync(name, port, cancellationToken);

                _logger.LogDebug("Discovered sitecontainer {name} at {address}", name, address);
                discoveredCount++;
            }
        }

        _logger.LogInformation("Refreshed sitecontainer addresses, found {count} containers", discoveredCount);
    }

    private static string GetRequiredEnvVar(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrEmpty(value))
        {
            throw new InvalidOperationException(
                $"Required environment variable '{name}' is not set. " +
                "This provider can only run inside Azure App Service.");
        }
        return value;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
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
