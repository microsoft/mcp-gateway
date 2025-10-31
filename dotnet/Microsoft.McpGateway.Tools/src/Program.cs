// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Azure.Identity;
using Microsoft.Azure.Cosmos;
using Microsoft.McpGateway.Management.Store;
using Microsoft.McpGateway.Tools.Contracts;
using Microsoft.McpGateway.Tools.Services;
using System.Text.Json;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

// Add logging
builder.Services.AddLogging();

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
app.MapMcp();
await app.RunAsync();
