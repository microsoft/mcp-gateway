// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Security.Claims;
using Azure.Identity;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Fluent;
using Microsoft.Extensions.Caching.Cosmos;
using Microsoft.Identity.Web;
using Microsoft.McpGateway.Management.Deployment;
using Microsoft.McpGateway.Management.Service;
using Microsoft.McpGateway.Management.Store;
using Microsoft.McpGateway.Service.Routing;
using Microsoft.McpGateway.Service.Session;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApplicationInsightsTelemetry();

builder.Services.AddSingleton<IKubernetesClientFactory, LocalKubernetesClientFactory>();
builder.Services.AddSingleton<IAdapterSessionStore, DistributedMemorySessionStore>();
builder.Services.AddSingleton<IServiceNodeInfoProvider, AdapterKubernetesNodeInfoProvider>();
builder.Services.AddSingleton<ISessionRoutingHandler, AdapterSessionRoutingHandler>();

if (builder.Environment.IsDevelopment())
{
    builder.Services.AddSingleton<IAdapterResourceStore, InMemoryAdapterResourceStore>();
    builder.Services.AddDistributedMemoryCache();
}
else
{
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"));

    builder.Services.AddSingleton<IAdapterResourceStore>(c =>
    {
        var config = builder.Configuration.GetSection("CosmosSettings");
        var connectionString = config["ConnectionString"];
        var client = string.IsNullOrEmpty(connectionString) ? new CosmosClient(config["AccountEndpoint"], new DefaultAzureCredential()) : new CosmosClient(connectionString);
        return new CosmosAdapterResourceStore(client, config["DatabaseName"]!, "AdapterContainer", c.GetRequiredService<ILogger<CosmosAdapterResourceStore>>());
    });
    builder.Services.AddCosmosCache((CosmosCacheOptions options) =>
    {
        var config = builder.Configuration.GetSection("CosmosSettings");
        var endpoint = config["AccountEndpoint"];
        var connectionString = config["ConnectionString"];

        options.ContainerName = "CacheContainer";
        options.DatabaseName = config["DatabaseName"]!;
        options.CreateIfNotExists = true;

        options.ClientBuilder = string.IsNullOrEmpty(connectionString) ? new CosmosClientBuilder(endpoint, new DefaultAzureCredential()) : new CosmosClientBuilder(connectionString);
    });
}

builder.Services.AddSingleton<IKubeClientWrapper>(c =>
{
    var kubeClientFactory = c.GetRequiredService<IKubernetesClientFactory>();
    return new KubeClient(kubeClientFactory, "adapter");
});
builder.Services.AddSingleton<IAdapterDeploymentManager>(c =>
{
    var config = builder.Configuration.GetSection("ContainerRegistrySettings");
    return new KubernetesAdapterDeploymentManager(config["Endpoint"]!, c.GetRequiredService<IKubeClientWrapper>(), c.GetRequiredService<ILogger<KubernetesAdapterDeploymentManager>>());
});
builder.Services.AddSingleton<IAdapterManagementService, AdapterManagementService>();
builder.Services.AddSingleton<IAdapterRichResultProvider, AdapterRichResultProvider>();

builder.Services.AddAuthorization();
builder.Services.AddControllers();
builder.Services.AddHttpClient();

builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(8000);
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.Use(async (context, next) =>
    {
        var devIdentity = new ClaimsIdentity("Development");
        devIdentity.AddClaim(new Claim(ClaimTypes.Name, "dev"));
        context.User = new ClaimsPrincipal(devIdentity);
        await next();
    });
}

// Configure the HTTP request pipeline.
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.Run();
