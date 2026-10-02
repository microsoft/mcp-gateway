using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.McpGateway.Management;
using Microsoft.McpGateway.Management.Contracts;

namespace Microsoft.McpGateway.Tools.Tests;

[TestClass]
public class McpProtocolMiddlewareTests
{
    [DataTestMethod]
    [DataRow(null, "server/discover", "server/discover", 400, -32020)]
    [DataRow("2025-06-18", "initialize", "initialize", 400, -32022)]
    [DataRow("2026-07-28", "initialize", "initialize", 400, -32601)]
    [DataRow("2026-07-28", "notifications/initialized", "notifications/initialized", 400, -32601)]
    [DataRow("2026-07-28", "tools/list", "initialize", 400, -32020)]
    [DataRow("2026-07-28", "server/discover", "server/discover", 200, 0)]
    public async Task HttpTransport_EnforcesModernProtocol(string? version, string headerMethod, string bodyMethod, int status, int errorCode)
    {
        await using var app = await StartServerAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/")
        {
            Content = JsonContent.Create(new
            {
                jsonrpc = "2.0",
                id = 1,
                method = bodyMethod,
                @params = new
                {
                    _meta = new Dictionary<string, object>
                    {
                        ["io.modelcontextprotocol/protocolVersion"] = McpProtocol.Version,
                        ["io.modelcontextprotocol/clientCapabilities"] = new { }
                    }
                }
            })
        };
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        request.Headers.TryAddWithoutValidation("Mcp-Method", headerMethod);
        if (version is not null)
            request.Headers.TryAddWithoutValidation(McpProtocol.VersionHeader, version);

        using var response = await client.SendAsync(request);

        Assert.AreEqual(status, (int)response.StatusCode);
        Assert.IsFalse(response.Headers.Contains("Mcp-Session-Id"));
        if (errorCode != 0)
        {
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.AreEqual(errorCode, body.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        }
        else
        {
            StringAssert.Contains(await response.Content.ReadAsStringAsync(), McpProtocol.Version);
        }
    }

    [DataTestMethod]
    [DataRow("GET")]
    [DataRow("DELETE")]
    public async Task HttpTransport_RejectsLegacyHttpMethods(string method)
    {
        await using var app = await StartServerAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using var request = new HttpRequestMessage(new HttpMethod(method), "/");

        using var response = await client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    private static async Task<WebApplication> StartServerAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddMcpServer(options => options.ProtocolVersion = McpProtocol.Version)
            .WithHttpTransport(options => options.Stateless = true);
        var app = builder.Build();
        app.UseMiddleware<McpProtocolMiddleware>();
        app.MapMcp();
        await app.StartAsync();
        return app;
    }
}