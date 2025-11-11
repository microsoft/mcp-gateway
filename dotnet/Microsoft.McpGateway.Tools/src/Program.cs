// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Azure.Identity;
using Microsoft.Azure.Cosmos;
using Microsoft.McpGateway.Management.Store;
using Microsoft.McpGateway.Management.Authorization;
using Microsoft.McpGateway.Tools.Contracts;
using Microsoft.McpGateway.Tools.Services;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

// Add logging
builder.Services.AddLogging();

builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<IPermissionProvider, SimplePermissionProvider>();
// Add HttpClient for tool execution
builder.Services.AddHttpClient();

// Configure tool resource store and tool definition provider
if (builder.Environment.IsDevelopment())
{
    var redisConnection = builder.Configuration.GetValue<string>("Redis:ConnectionString") ?? "localhost:6379";
    builder.Services.AddStackExchangeRedisCache(options =>
    {
        options.Configuration = redisConnection;
        options.InstanceName = "mcpgateway:";
    });
    
    // Use Redis-backed store that can be shared with the gateway service
    builder.Services.AddSingleton<IToolResourceStore, RedisToolResourceStore>();
    
    builder.Logging.AddConsole();
    builder.Logging.SetMinimumLevel(LogLevel.Debug);
}
else
{
    // In production, use Cosmos DB store
    var config = builder.Configuration.GetSection("CosmosSettings");
    var credential = new DefaultAzureCredential();
    var cosmosClient = new CosmosClient(config["AccountEndpoint"], credential, new CosmosClientOptions
    {
        Serializer = new CosmosSystemTextJsonSerializer(new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        })
    });

    // Register IToolResourceStore
    builder.Services.AddSingleton<IToolResourceStore>(sp =>
    {
        var logger = sp.GetRequiredService<ILogger<CosmosToolResourceStore>>();
        return new CosmosToolResourceStore(
            cosmosClient,
            config["DatabaseName"]!,
            "ToolContainer",
            logger);
    });
}

// Register IToolDefinitionProvider using the store
builder.Services.AddSingleton<IToolDefinitionProvider, StorageToolDefinitionProvider>();

// Register tool executor
builder.Services.AddSingleton<IToolExecutor, HttpToolExecutor>();

builder.Services.AddMcpServer()
    .WithListToolsHandler(static (c, ct) =>
    {
        var toolDefinitionProvider = c.Services!.GetRequiredService<IToolDefinitionProvider>();
        return toolDefinitionProvider?.ListToolsAsync(c, ct) ?? throw new InvalidOperationException("Tool registry not properly registered.");
    })
    .WithCallToolHandler(static (c, ct) =>
    {
        var toolExecutor = c.Services!.GetRequiredService<IToolExecutor>();
        return toolExecutor?.ExecuteToolAsync(c, ct) ?? throw new InvalidOperationException("Tool executor not properly registered.");
    })
    .WithHttpTransport();


builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(8000);
});

var app = builder.Build();

app.Use(async (context, next) =>
{
    if (context.Request.Headers.TryGetValue(ForwardedIdentityHeaders.UserId, out var forwardedUserId) && !string.IsNullOrWhiteSpace(forwardedUserId))
    {
        var userId = forwardedUserId.ToString().Trim();
        if (!string.IsNullOrWhiteSpace(userId))
        {
            var identity = new ClaimsIdentity("Forwarded");
            identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, userId));

            if (context.Request.Headers.TryGetValue(ForwardedIdentityHeaders.Roles, out var forwardedRoles))
            {
                var roles = forwardedRoles.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var role in roles.Where(role => !string.IsNullOrWhiteSpace(role)))
                {
                    identity.AddClaim(new Claim(ClaimTypes.Role, role));
                }
            }

            context.User = new ClaimsPrincipal(identity);
        }
    }

    await next().ConfigureAwait(false);
});

if (app.Environment.IsDevelopment())
{
    app.Use(async (context, next) =>
    {
        if (!context.User?.Identities?.Any(identity => identity.IsAuthenticated) ?? true)
        {
            var devIdentity = new ClaimsIdentity("Development");
            devIdentity.AddClaim(new Claim(ClaimTypes.NameIdentifier, "dev"));
            devIdentity.AddClaim(new Claim(ClaimTypes.Name, "dev"));
            devIdentity.AddClaim(new Claim(ClaimTypes.Role, "mcp.dev"));
            context.User = new ClaimsPrincipal(devIdentity);
        }

        await next().ConfigureAwait(false);
    });
}
app.MapMcp();
await app.RunAsync();
