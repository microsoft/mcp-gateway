using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.McpGateway.Management.Contracts;

namespace Microsoft.McpGateway.Service.Tests;

[TestClass]
public class McpEndpointMiddlewareTests
{
    [DataTestMethod]
    [DataRow(null, 400, -32020)]
    [DataRow("2025-06-18", 400, -32022)]
    [DataRow("2099-01-01", 400, -32022)]
    [DataRow("2026-07-28", 200, 0)]
    public async Task Invoke_RequiresModernVersion(string? version, int status, int errorCode)
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var context = CreateContext(services);
        if (version != null)
            context.Request.Headers[McpProtocol.VersionHeader] = version;
        var invoked = false;
        var middleware = CreateMiddleware(_ => { invoked = true; return Task.CompletedTask; });

        await middleware.InvokeAsync(context);

        Assert.AreEqual(status, context.Response.StatusCode);
        Assert.AreEqual(status == 200, invoked);
        if (errorCode != 0)
        {
            using var result = JsonDocument.Parse(((MemoryStream)context.Response.Body).ToArray());
            Assert.AreEqual(errorCode, result.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        }
    }

    [DataTestMethod]
    [DataRow("GET")]
    [DataRow("DELETE")]
    public async Task Invoke_RejectsLegacyHttpMethods(string method)
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var context = CreateContext(services);
        context.Request.Method = method;
        await CreateMiddleware(_ => throw new AssertFailedException("Request must not reach a backend.")).InvokeAsync(context);
        Assert.AreEqual(405, context.Response.StatusCode);
        Assert.AreEqual("POST", context.Response.Headers.Allow.ToString());
    }

    [DataTestMethod]
    [DataRow("https://portal.example", 200)]
    [DataRow("https://evil.example", 403)]
    [DataRow("null", 403)]
    public async Task Invoke_ValidatesSuppliedOrigin(string origin, int status)
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var context = CreateContext(services);
        context.Request.Headers[McpProtocol.VersionHeader] = McpProtocol.Version;
        context.Request.Headers.Origin = origin;
        await CreateMiddleware(_ => Task.CompletedTask).InvokeAsync(context);
        Assert.AreEqual(status, context.Response.StatusCode);
    }

    [TestMethod]
    public async Task Invoke_DoesNotChangeApplicationSessionEndpoints()
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var context = CreateContext(services);
        context.Request.Path = "/sessions/run";
        var invoked = false;
        await CreateMiddleware(_ => { invoked = true; return Task.CompletedTask; }).InvokeAsync(context);
        Assert.IsTrue(invoked);
    }

    private static DefaultHttpContext CreateContext(IServiceProvider services)
    {
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Method = "POST";
        context.Request.Path = "/adapters/example/mcp";
        context.Request.Headers["Mcp-Method"] = "server/discover";
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static McpEndpointMiddleware CreateMiddleware(RequestDelegate next) => new(next,
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["PublicOrigin"] = "https://portal.example/"
        }).Build());
}