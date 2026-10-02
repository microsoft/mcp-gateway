using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.AspNetCore.Authentication;

namespace Microsoft.McpGateway.Service.Tests;

[TestClass]
public class McpAuthenticationTests
{
    [TestMethod]
    public async Task Challenge_UsesConfiguredPublicHttpsOriginBehindProxy()
    {
        using var services = CreateServices();
        var context = CreateContext(services, "/adapters/example/mcp");

        await context.ChallengeAsync("Mcp");

        Assert.AreEqual(401, context.Response.StatusCode);
        StringAssert.Contains(context.Response.Headers.WWWAuthenticate.ToString(),
            "resource_metadata=\"https://gateway.example/.well-known/oauth-protected-resource/adapters/example/mcp\"");
        Assert.IsFalse(context.Response.Headers.WWWAuthenticate.ToString().Contains("internal-host"));
    }

    [TestMethod]
    public async Task Metadata_DoesNotInterceptPrefixLookalikePaths()
    {
        using var services = CreateServices();
        var context = CreateContext(services, "/.well-known/oauth-protected-resource-invalid");
        var handler = await services.GetRequiredService<IAuthenticationHandlerProvider>().GetHandlerAsync(context, "Mcp");

        Assert.IsFalse(await ((IAuthenticationRequestHandler)handler!).HandleRequestAsync());
    }

    private static ServiceProvider CreateServices()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddAuthentication().AddScheme<McpAuthenticationOptions, McpSubPathAwareAuthenticationHandler>("Mcp", options =>
        {
            options.ResourceMetadataUri = new Uri("/.well-known/oauth-protected-resource", UriKind.Relative);
            options.ResourceMetadata = new()
            {
                Resource = "https://gateway.example/",
                AuthorizationServers = ["https://login.microsoftonline.com/test/v2.0"],
                ScopesSupported = ["api://test/access"]
            };
        });
        return services.BuildServiceProvider();
    }

    private static DefaultHttpContext CreateContext(IServiceProvider services, string path)
    {
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("internal-host");
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        return context;
    }
}