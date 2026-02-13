// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Azure;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.AppService;
using Azure.ResourceManager.AppService.Models;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.McpGateway.Management.Contracts;
using Microsoft.McpGateway.Management.Deployment;
using Microsoft.McpGateway.Management.Extensions;

namespace Microsoft.McpGateway.Service.AppService;

/// <summary>
/// Manages adapter deployments as App Service sitecontainers.
/// Uses Azure ARM API to dynamically create/update/delete sidecar containers.
/// </summary>
public class AppServiceDeploymentManager : IAdapterDeploymentManager
{
    private readonly ArmClient _armClient;
    private readonly string _subscriptionId;
    private readonly string _resourceGroupName;
    private readonly string _webAppName;
    private readonly string _containerRegistryAddress;
    private readonly string? _userAssignedManagedIdentityClientId;
    private readonly SiteContainerPortAllocator _portAllocator;
    private readonly ILogger<AppServiceDeploymentManager> _logger;

    public AppServiceDeploymentManager(
        string containerRegistryAddress,
        SiteContainerPortAllocator portAllocator,
        ILogger<AppServiceDeploymentManager> logger)
    {
        ArgumentException.ThrowIfNullOrEmpty(containerRegistryAddress);

        _containerRegistryAddress = containerRegistryAddress;
        _portAllocator = portAllocator;
        _logger = logger;

        // Use managed identity for ARM access
        _armClient = new ArmClient(new DefaultAzureCredential());

        // Get app service context from environment variables
        _subscriptionId = GetRequiredEnvVar("WEBSITE_OWNER_NAME").Split('+')[0];
        _resourceGroupName = GetRequiredEnvVar("WEBSITE_RESOURCE_GROUP");
        _webAppName = GetRequiredEnvVar("WEBSITE_SITE_NAME");

        // Get optional managed identity client ID for ACR auth
        _userAssignedManagedIdentityClientId = Environment.GetEnvironmentVariable("AZURE_CLIENT_ID");

        _logger.LogInformation(
            "AppServiceDeploymentManager initialized for {subscription}/{resourceGroup}/{webApp}",
            _subscriptionId, _resourceGroupName, _webAppName);
    }

    public async Task CreateDeploymentAsync(AdapterData request, ResourceType resourceType, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Creating sitecontainer deployment for {name} with resource type {resourceType}",
            request.Name.Sanitize(), resourceType.ToString().ToLowerInvariant());

        var webApp = await GetWebAppAsync(cancellationToken);
        var containers = webApp.GetSiteContainers();

        // Check if container already exists - if so, just save the port mapping and skip ARM update
        // This avoids causing App Service restarts when the sidecar is already running
        try
        {
            var existing = await containers.GetAsync(request.Name, cancellationToken);
            if (existing.Value != null && int.TryParse(existing.Value.Data.TargetPort, out var existingPort))
            {
                _logger.LogInformation(
                    "Sitecontainer {name} already exists on port {port}, skipping creation to avoid restart",
                    request.Name.Sanitize(), existingPort);

                // Save the existing port mapping for routing
                await _portAllocator.SavePortMappingAsync(request.Name, existingPort, cancellationToken);
                return;
            }
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // Container doesn't exist, proceed with creation
            _logger.LogInformation("Sitecontainer {name} does not exist, creating new", request.Name.Sanitize());
        }

        // Allocate a port for this adapter
        var port = await _portAllocator.AllocatePortAsync(request.Name, containers, cancellationToken);

        // Create the sitecontainer with ARM API
        var containerData = CreateSiteContainerData(request, port);

        try
        {
            await containers.CreateOrUpdateAsync(
                WaitUntil.Completed,
                request.Name,
                containerData,
                cancellationToken);

            // Save port mapping for routing
            await _portAllocator.SavePortMappingAsync(request.Name, port, cancellationToken);

            _logger.LogInformation(
                "Created sitecontainer {name} on port {port}",
                request.Name.Sanitize(), port);
        }
        catch (RequestFailedException ex)
        {
            _logger.LogError(ex, "Failed to create sitecontainer {name}", request.Name.Sanitize());
            throw;
        }
    }

    public async Task UpdateDeploymentAsync(AdapterData request, ResourceType resourceType, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Updating sitecontainer deployment for {name}",
            request.Name.Sanitize());

        var webApp = await GetWebAppAsync(cancellationToken);
        var containers = webApp.GetSiteContainers();

        // Get existing port from cache or discover from ARM
        var port = await _portAllocator.GetPortAsync(request.Name, cancellationToken);
        if (port == null)
        {
            // Try to find from existing container
            try
            {
                var existing = await containers.GetAsync(request.Name, cancellationToken);
                if (int.TryParse(existing.Value.Data.TargetPort, out var existingPort))
                {
                    port = existingPort;
                }
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                _logger.LogWarning("Container {name} not found for update, creating new", request.Name.Sanitize());
                await CreateDeploymentAsync(request, resourceType, cancellationToken);
                return;
            }
        }

        var containerData = CreateSiteContainerData(request, port ?? 8001);

        try
        {
            await containers.CreateOrUpdateAsync(
                WaitUntil.Completed,
                request.Name,
                containerData,
                cancellationToken);

            _logger.LogInformation("Updated sitecontainer {name}", request.Name.Sanitize());
        }
        catch (RequestFailedException ex)
        {
            _logger.LogError(ex, "Failed to update sitecontainer {name}", request.Name.Sanitize());
            throw;
        }
    }

    public async Task DeleteDeploymentAsync(string name, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Deleting sitecontainer deployment for {name}", name.Sanitize());

        try
        {
            var webApp = await GetWebAppAsync(cancellationToken);
            var container = await webApp.GetSiteContainerAsync(name, cancellationToken);

            await container.Value.DeleteAsync(WaitUntil.Completed, cancellationToken);
            await _portAllocator.ReleasePortAsync(name, cancellationToken);

            _logger.LogInformation("Deleted sitecontainer {name}", name.Sanitize());
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            _logger.LogWarning("Sitecontainer {name} does not exist, nothing to delete", name.Sanitize());
        }
    }

    public async Task<AdapterStatus> GetDeploymentStatusAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            var webApp = await GetWebAppAsync(cancellationToken);
            var container = await webApp.GetSiteContainerAsync(name, cancellationToken);
            var data = container.Value.Data;

            return new AdapterStatus
            {
                // App Service doesn't have replica concept like K8s
                ReadyReplicas = data.IsMain == true ? 1 : 1,
                UpdatedReplicas = 1,
                AvailableReplicas = 1,
                Image = data.Image,
                ReplicaStatus = "Running (App Service sidecar)"
            };
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return new AdapterStatus
            {
                ReadyReplicas = 0,
                AvailableReplicas = 0,
                ReplicaStatus = "NotFound"
            };
        }
    }

    public Task<string> GetDeploymentLogsAsync(string name, int ordinal, CancellationToken cancellationToken)
    {
        // App Service logs are accessed via Kudu or Log Stream, not ARM API
        // Return guidance for users
        var logUrl = $"https://{_webAppName}.scm.azurewebsites.net/api/logs/docker";
        return Task.FromResult(
            $"Logs for sitecontainer '{name}' are available via App Service Log Stream:\n" +
            $"  Kudu: {logUrl}\n" +
            $"  CLI: az webapp log tail --name {_webAppName} --resource-group {_resourceGroupName}");
    }

    private SiteContainerData CreateSiteContainerData(AdapterData request, int port)
    {
        var image = $"{_containerRegistryAddress}/{request.ImageName}:{request.ImageVersion}";

        var containerData = new SiteContainerData
        {
            Image = image,
            TargetPort = port.ToString(),
            IsMain = false,  // MCP servers are sidecars, not the main container
        };

        // Use user-assigned managed identity for ACR auth if available
        if (!string.IsNullOrEmpty(_userAssignedManagedIdentityClientId))
        {
            containerData.AuthType = SiteContainerAuthType.UserAssigned;
            containerData.UserManagedIdentityClientId = _userAssignedManagedIdentityClientId;
        }
        else
        {
            containerData.AuthType = SiteContainerAuthType.Anonymous;
        }

        // Add environment variables
        foreach (var (key, value) in request.EnvironmentVariables)
        {
            containerData.EnvironmentVariables.Add(new WebAppEnvironmentVariable(key, value));
        }

        // Add PORT env var so the MCP server knows which port to listen on
        if (!request.EnvironmentVariables.ContainsKey("PORT"))
        {
            containerData.EnvironmentVariables.Add(new WebAppEnvironmentVariable("PORT", port.ToString()));
        }

        return containerData;
    }

    private async Task<WebSiteResource> GetWebAppAsync(CancellationToken cancellationToken)
    {
        var resourceId = WebSiteResource.CreateResourceIdentifier(
            _subscriptionId, _resourceGroupName, _webAppName);
        return await _armClient.GetWebSiteResource(resourceId).GetAsync(cancellationToken);
    }

    private static string GetRequiredEnvVar(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrEmpty(value))
        {
            throw new InvalidOperationException(
                $"Required environment variable '{name}' is not set. " +
                "This manager can only run inside Azure App Service.");
        }
        return value;
    }
}
