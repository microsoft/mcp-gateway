// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Azure.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Fluent;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Identity.Web;
using Microsoft.McpGateway.Management.Authorization;
using Microsoft.McpGateway.Management.Deployment;
using Microsoft.McpGateway.Management.Service;
using Microsoft.McpGateway.Management.Store;
using Microsoft.McpGateway.Service.Authentication;
using Microsoft.McpGateway.Service.Routing;
using Microsoft.McpGateway.Service.Session;
using ModelContextProtocol.AspNetCore.Authentication;
using System.Text.Json;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);
var credential = new DefaultAzureCredential();

builder.Services.AddApplicationInsightsTelemetry();
builder.Services.AddLogging();

builder.Services.AddSingleton<IKubernetesClientFactory, LocalKubernetesClientFactory>();
builder.Services.AddSingleton<IAdapterSessionStore, DistributedMemorySessionStore>();
builder.Services.AddSingleton<IServiceNodeInfoProvider, AdapterKubernetesNodeInfoProvider>();
builder.Services.AddSingleton<ISessionRoutingHandler, AdapterSessionRoutingHandler>();

var storeBackend = builder.Configuration.GetValue<string>("StoreBackend") ?? "";

if (builder.Environment.IsDevelopment())
{
    builder.Services
        .AddAuthentication(DevelopmentAuthenticationHandler.SchemeName)
        .AddScheme<AuthenticationSchemeOptions, DevelopmentAuthenticationHandler>(DevelopmentAuthenticationHandler.SchemeName, null);    

    if (storeBackend.Equals("Postgres", StringComparison.OrdinalIgnoreCase))
    {
        var postgresConnection = builder.Configuration.GetValue<string>("Postgres:ConnectionString")
            ?? throw new InvalidOperationException("Postgres:ConnectionString is required when StoreBackend is Postgres.");

        builder.Services.AddDistributedPostgresCache(options =>
        {
            options.ConnectionString = postgresConnection;
            options.SchemaName = "public";
            options.TableName = "mcp_gateway_cache";
            options.CreateIfNotExists = true;
        });

        builder.Services.AddSingleton<IAdapterResourceStore>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<DistributedAdapterResourceStore>>();
            return new DistributedAdapterResourceStore(sp.GetRequiredService<IDistributedCache>(), logger);
        });

        builder.Services.AddSingleton<IToolResourceStore>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<DistributedToolResourceStore>>();
            return new DistributedToolResourceStore(sp.GetRequiredService<IDistributedCache>(), logger);
        });

    }
    else
    {
        var redisConnection = builder.Configuration.GetValue<string>("Redis:ConnectionString") ?? "localhost:6379";
        builder.Services.AddStackExchangeRedisCache(options =>
        {
            options.Configuration = redisConnection;
            options.InstanceName = "mcpgateway:";
        });

        builder.Services.AddSingleton<IAdapterResourceStore, RedisAdapterResourceStore>();
        builder.Services.AddSingleton<IToolResourceStore, RedisToolResourceStore>();
    }

    builder.Logging.AddConsole();
    builder.Logging.SetMinimumLevel(LogLevel.Debug);
}
else
{
    var azureAdConfig = builder.Configuration.GetSection("AzureAd");
    builder.Services.AddAuthentication(options =>
    {
        options.DefaultChallengeScheme = McpAuthenticationDefaults.AuthenticationScheme;
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddScheme<McpAuthenticationOptions, McpSubPathAwareAuthenticationHandler>(
        McpAuthenticationDefaults.AuthenticationScheme,
        McpAuthenticationDefaults.DisplayName,
    options =>
    {
        options.ResourceMetadata = new()
        {
            Resource = new Uri(builder.Configuration.GetValue<string>("PublicOrigin")!),
            AuthorizationServers = { new Uri($"https://login.microsoftonline.com/{azureAdConfig["TenantId"]}/v2.0") },
            ScopesSupported = [$"api://{azureAdConfig["ClientId"]}/.default"]
        };
    })
    .AddMicrosoftIdentityWebApi(azureAdConfig);

    if (storeBackend.Equals("Postgres", StringComparison.OrdinalIgnoreCase))
    {
        var postgresConnection = builder.Configuration.GetValue<string>("Postgres:ConnectionString")
            ?? throw new InvalidOperationException("Postgres:ConnectionString is required when StoreBackend is Postgres.");

        builder.Services.AddDistributedPostgresCache(options =>
        {
            options.ConnectionString = postgresConnection;
            options.SchemaName = "public";
            options.TableName = "mcp_gateway_cache";
            options.CreateIfNotExists = true;
        });

        builder.Services.AddSingleton<IAdapterResourceStore>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<DistributedAdapterResourceStore>>();
            return new DistributedAdapterResourceStore(sp.GetRequiredService<IDistributedCache>(), logger);
        });

        builder.Services.AddSingleton<IToolResourceStore>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<DistributedToolResourceStore>>();
            return new DistributedToolResourceStore(sp.GetRequiredService<IDistributedCache>(), logger);
        });

    }
    else {

        // Create CosmosClient with credential-based authentication
        var cosmosConfig = builder.Configuration.GetSection("CosmosSettings");
        var cosmosClient = new CosmosClient(
            cosmosConfig["AccountEndpoint"], 
            credential, 
            new CosmosClientOptions
            {
                Serializer = new CosmosSystemTextJsonSerializer(new JsonSerializerOptions
                {
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                })
            });

        builder.Services.AddSingleton<IAdapterResourceStore>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<CosmosAdapterResourceStore>>();
            return new CosmosAdapterResourceStore(cosmosClient, cosmosConfig["DatabaseName"]!, "AdapterContainer", logger);
        });

        builder.Services.AddSingleton<IToolResourceStore>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<CosmosToolResourceStore>>();
            return new CosmosToolResourceStore(cosmosClient, cosmosConfig["DatabaseName"]!, "ToolContainer", logger);
        });
    
        builder.Services.AddCosmosCache(options =>
        {
            options.ContainerName = "CacheContainer";
            options.DatabaseName = cosmosConfig["DatabaseName"]!;
            options.CreateIfNotExists = true;
            options.ClientBuilder = new CosmosClientBuilder(cosmosConfig["AccountEndpoint"], credential);
        });
    }
}

builder.Services.AddSingleton<IKubeClientWrapper>(c =>
{
    var kubeClientFactory = c.GetRequiredService<IKubernetesClientFactory>();
    return new KubeClient(kubeClientFactory, "adapter");
});
builder.Services.AddSingleton<IPermissionProvider, SimplePermissionProvider>();
builder.Services.AddSingleton<IAdapterDeploymentManager>(c =>
{
    var config = builder.Configuration.GetSection("ContainerRegistrySettings");
    return new KubernetesAdapterDeploymentManager(config["Endpoint"]!, c.GetRequiredService<IKubeClientWrapper>(), c.GetRequiredService<ILogger<KubernetesAdapterDeploymentManager>>());
});
builder.Services.AddSingleton<IAdapterManagementService, AdapterManagementService>();
builder.Services.AddSingleton<IToolManagementService, ToolManagementService>();
builder.Services.AddSingleton<IAdapterRichResultProvider, AdapterRichResultProvider>();

builder.Services.AddAuthorization();
builder.Services.AddControllers();
builder.Services.AddHttpClient();

builder.WebHost.ConfigureKestrel(options =>
{
    var port = builder.Configuration.GetValue<int?>("Kestrel:Port") ?? 8000;
    options.ListenAnyIP(port);
});

var app = builder.Build();

// Configure the HTTP request pipeline.
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
await app.RunAsync();
