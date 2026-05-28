// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Azure.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Fluent;
using Microsoft.Identity.Web;
using Microsoft.McpGateway.Management.Authorization;
using Microsoft.McpGateway.Management.Deployment;
using Microsoft.McpGateway.Management.Foundry;
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

// Operators can opt out of Entra ID and run the gateway with the dev auth
// handler (X-Dev-* headers) by setting `Authentication__BypassEntra=true`
// in the pod's environment. This is intended for restricted demo / e2e
// clusters that aren't reachable from the public internet. Always-on in
// Development as before.
var bypassEntra = builder.Configuration.GetValue<bool>("Authentication:BypassEntra");
var useDevAuth = builder.Environment.IsDevelopment() || bypassEntra;

if (useDevAuth)
{
    if (bypassEntra && !builder.Environment.IsDevelopment())
    {
        Console.WriteLine("[auth] Authentication:BypassEntra=true; using Development auth scheme (X-Dev-* headers). Do NOT enable this on internet-facing deployments.");
    }

    builder.Services
        .AddAuthentication(DevelopmentAuthenticationHandler.SchemeName)
        .AddScheme<AuthenticationSchemeOptions, DevelopmentAuthenticationHandler>(DevelopmentAuthenticationHandler.SchemeName, null);
}

if (builder.Environment.IsDevelopment())
{

    // The shipped appsettings.Development.json points Redis at the in-cluster
    // `redis-service` hostname so the same image works inside the local
    // kind/k3s deployment, but a vanilla `dotnet run` on a laptop has no
    // Redis. Probe the configured endpoint with a short timeout: if it's
    // reachable, use the Redis-backed stores; otherwise transparently fall
    // back to in-memory so the gateway and management portal still work.
    //
    // The probe only runs in Development; production / cloud always uses
    // Cosmos and never goes through this branch.
    var redisConnection = builder.Configuration.GetValue<string>("Redis:ConnectionString") ?? "localhost:6379";
    var useInMemoryStoresSetting = builder.Configuration.GetValue<bool?>("Storage:UseInMemoryStores");
    var useInMemoryStores = useInMemoryStoresSetting ?? !TryProbeRedis(redisConnection);

    if (useInMemoryStores)
    {
        Console.WriteLine($"[dev] Redis at '{redisConnection}' unavailable or disabled; using in-memory stores.");
        builder.Services.AddDistributedMemoryCache();
        builder.Services.AddSingleton<IAdapterResourceStore, InMemoryAdapterResourceStore>();
        builder.Services.AddSingleton<IToolResourceStore, InMemoryToolResourceStore>();
    }
    else
    {
        Console.WriteLine($"[dev] Using Redis at '{redisConnection}' for adapter/tool stores.");
        builder.Services.AddStackExchangeRedisCache(options =>
        {
            options.Configuration = redisConnection;
            options.InstanceName = "mcpgateway:";
        });

        builder.Services.AddSingleton<IAdapterResourceStore, RedisAdapterResourceStore>();
        builder.Services.AddSingleton<IToolResourceStore, RedisToolResourceStore>();
    }

    builder.Services.AddSingleton<IAgentResourceStore, InMemoryAgentResourceStore>();
    builder.Services.AddSingleton<ISessionResourceStore, InMemorySessionResourceStore>();

    builder.Logging.AddConsole();
    builder.Logging.SetMinimumLevel(LogLevel.Debug);
}
else
{
    if (!useDevAuth)
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
    }

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

    builder.Services.AddSingleton<IAgentResourceStore>(sp =>
    {
        var logger = sp.GetRequiredService<ILogger<CosmosAgentResourceStore>>();
        return new CosmosAgentResourceStore(cosmosClient, cosmosConfig["DatabaseName"]!, "AgentContainer", logger);
    });

    builder.Services.AddSingleton<ISessionResourceStore>(sp =>
    {
        var logger = sp.GetRequiredService<ILogger<CosmosSessionResourceStore>>();
        return new CosmosSessionResourceStore(cosmosClient, cosmosConfig["DatabaseName"]!, "SessionContainer", logger);
    });
    
    builder.Services.AddCosmosCache(options =>
    {
        options.ContainerName = "CacheContainer";
        options.DatabaseName = cosmosConfig["DatabaseName"]!;
        options.CreateIfNotExists = true;
        options.ClientBuilder = new CosmosClientBuilder(cosmosConfig["AccountEndpoint"], credential);
    });
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
builder.Services.AddSingleton<IAgentManagementService, AgentManagementService>();
builder.Services.AddSingleton<ISessionManagementService, SessionManagementService>();
builder.Services.AddSingleton<IAdapterRichResultProvider, AdapterRichResultProvider>();

// Foundry chat client. Only registered when an endpoint is configured so that
// dev / unit-test environments can run without it; SessionManagementService
// gracefully leaves sessions in Pending when no client is wired up.
var foundrySection = builder.Configuration.GetSection("FoundrySettings");
if (!string.IsNullOrWhiteSpace(foundrySection["Endpoint"]))
{
    builder.Services.Configure<FoundrySettings>(foundrySection);
    builder.Services.AddSingleton<Azure.Core.TokenCredential>(credential);
    builder.Services.AddSingleton<IFoundryChatClient, FoundryChatClient>();
    builder.Services.AddSingleton<BuiltinToolExecutor>();
    builder.Services.AddSingleton<AgentToolRegistry>();
    builder.Services.AddSingleton<AgentRunner>();
    builder.Services.AddSingleton<Func<AgentRunner>>(sp => () => sp.GetRequiredService<AgentRunner>());
    builder.Services.AddSingleton<SubAgentInvoker>();
}

builder.Services.AddAuthorization();
builder.Services.AddControllers();
builder.Services.AddHttpClient();

builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(8000);
});

var app = builder.Build();

// Serve the management portal SPA out of wwwroot/portal. Static files are
// mapped before authentication so the HTML shell, JS bundle and runtime
// config endpoint are reachable anonymously; the SPA acquires its own
// access token via MSAL once it loads.
app.UseStaticFiles();

// Configure the HTTP request pipeline.
app.UseAuthentication();
app.UseAuthorization();

// Land users on the portal when they visit the gateway in a browser. The
// redirect is attributed AllowAnonymous so the unauthenticated case still
// reaches the SPA shell instead of the 401 challenge handler.
app.MapGet("/", () => Results.Redirect("/portal/", permanent: false))
    .AllowAnonymous();

app.MapControllers();

// Any /portal/* path that didn't match a physical file or controller route
// is treated as a SPA route and served the portal shell. The `nonfile`
// constraint keeps requests for hashed asset bundles flowing through the
// static-file middleware instead. Constrained to /portal/* so requests like
// `GET /adapters/foo` continue to hit the API controllers untouched.
app.MapFallbackToFile("/portal", "portal/index.html").AllowAnonymous();
app.MapFallbackToFile("/portal/{*path:nonfile}", "portal/index.html")
    .AllowAnonymous();
await app.RunAsync();

// Best-effort check that a configured Redis is reachable. Only used by the
// Development-mode store registration above so a developer running
// `dotnet run` on a laptop transparently falls back to in-memory stores
// instead of failing every /adapters and /tools call with a Redis timeout.
static bool TryProbeRedis(string connectionString)
{
    try
    {
        var options = StackExchange.Redis.ConfigurationOptions.Parse(connectionString);
        options.AbortOnConnectFail = false;
        options.ConnectTimeout = 2000;
        options.SyncTimeout = 2000;
        options.ConnectRetry = 1;
        using var muxer = StackExchange.Redis.ConnectionMultiplexer.Connect(options);
        return muxer.IsConnected;
    }
    catch
    {
        return false;
    }
}
