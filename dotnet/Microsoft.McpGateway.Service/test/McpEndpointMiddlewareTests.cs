using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    [DataTestMethod]
    [DataRow("initialize", "/mcp")]
    [DataRow("initialize", "/adapters/example/mcp")]
    [DataRow("notifications/initialized", "/mcp")]
    [DataRow("notifications/initialized", "/adapters/example/mcp")]
    public async Task Invoke_RejectsLegacyHandshakeWithModernVersion(string method, string path)
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var context = CreateContext(services);
        context.Request.Path = path;
        context.Request.Headers[McpProtocol.VersionHeader] = McpProtocol.Version;
        context.Request.Headers["Mcp-Method"] = method;

        await CreateMiddleware(_ => throw new AssertFailedException("Request must not reach a backend.")).InvokeAsync(context);

        Assert.AreEqual(400, context.Response.StatusCode);
        using var result = JsonDocument.Parse(((MemoryStream)context.Response.Body).ToArray());
        Assert.AreEqual(-32601, result.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [DataTestMethod]
    [DataRow(null, 406)]
    [DataRow("application/json", 406)]
    [DataRow("text/event-stream", 406)]
    [DataRow("*/*", 406)]
    [DataRow("application/json, text/event-stream;q=0", 406)]
    [DataRow("application/json, text/event-stream, invalid", 406)]
    [DataRow("application/json, text/event-stream", 200)]
    [DataRow("Application/Json; charset=utf-8, Text/Event-Stream;q=0.5", 200)]
    public async Task Invoke_RequiresResponseMediaTypes(string? accept, int status)
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var context = CreateContext(services);
        context.Request.Headers[McpProtocol.VersionHeader] = McpProtocol.Version;
        context.Request.Headers.Accept = accept;
        var invoked = false;

        await CreateMiddleware(_ => { invoked = true; return Task.CompletedTask; }).InvokeAsync(context);

        Assert.AreEqual(status, context.Response.StatusCode);
        Assert.AreEqual(status == 200, invoked);
    }

    [DataTestMethod]
    [DataRow("tools/list", "tools/call")]
    [DataRow("server/discover", "resources/read")]
    [DataRow("tools/list", "initialize")]
    [DataRow("tools/list", "notifications/initialized")]
    public async Task Invoke_RejectsHeaderBodyMethodMismatch(string headerMethod, string bodyMethod)
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var context = CreateContext(services);
        context.Request.Headers[McpProtocol.VersionHeader] = McpProtocol.Version;
        context.Request.Headers["Mcp-Method"] = headerMethod;
        SetBody(context, CreateMessage(bodyMethod));

        await AssertRejectedAsync(context, -32020);
    }

    [DataTestMethod]
    [DataRow("params", null)]
    [DataRow("params", "[]")]
    [DataRow("_meta", null)]
    [DataRow("_meta", "null")]
    [DataRow("io.modelcontextprotocol/protocolVersion", null)]
    [DataRow("io.modelcontextprotocol/protocolVersion", "42")]
    [DataRow("io.modelcontextprotocol/protocolVersion", "\"2025-06-18\"")]
    [DataRow("io.modelcontextprotocol/clientCapabilities", null)]
    [DataRow("io.modelcontextprotocol/clientCapabilities", "null")]
    [DataRow("io.modelcontextprotocol/clientCapabilities", "[]")]
    [DataRow("io.modelcontextprotocol/clientCapabilities", "false")]
    public async Task Invoke_RequiresValidPerRequestMetadata(string property, string? value)
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var context = CreateContext(services);
        context.Request.Headers[McpProtocol.VersionHeader] = McpProtocol.Version;
        var message = CreateMessage();
        var parent = property switch
        {
            "params" => message,
            "_meta" => message["params"]!.AsObject(),
            _ => message["params"]!["_meta"]!.AsObject()
        };
        if (value is null)
            parent.Remove(property);
        else
            parent[property] = JsonNode.Parse(value);
        SetBody(context, message);

        await AssertRejectedAsync(context, -32020);
    }

    [DataTestMethod]
    [DataRow("tools/call", "name", "example", "example", 200)]
    [DataRow("prompts/get", "name", "example", "example", 200)]
    [DataRow("resources/read", "uri", "file:///example", "file:///example", 200)]
    [DataRow("tools/call", "name", "caf\u00e9", "=?base64?Y2Fmw6k=?=", 200)]
    [DataRow("tools/call", "name", "example", null, 400)]
    [DataRow("tools/call", "name", "example", "other", 400)]
    [DataRow("prompts/get", "name", "example", "other", 400)]
    [DataRow("resources/read", "uri", "file:///example", "file:///other", 400)]
    [DataRow("tools/call", "name", null, "example", 400)]
    [DataRow("tools/call", "name", "example", "=?base64?invalid?=", 400)]
    [DataRow("tools/call", "name", "example", "=?base64?/w==?=", 400)]
    [DataRow("tools/call", "name", "example", "=?base64?=", 400)]
    [DataRow("tools/call", "name", "", "=?base64??=", 200)]
    public async Task Invoke_ValidatesMirroredName(string method, string property, string? bodyName, string? headerName, int status)
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var context = CreateContext(services);
        context.Request.Headers[McpProtocol.VersionHeader] = McpProtocol.Version;
        context.Request.Headers["Mcp-Method"] = method;
        context.Request.Headers["Mcp-Name"] = headerName;
        var message = CreateMessage(method);
        message["params"]![property] = bodyName;
        SetBody(context, message);
        var invoked = false;

        await CreateMiddleware(_ => { invoked = true; return Task.CompletedTask; }).InvokeAsync(context);

        Assert.AreEqual(status, context.Response.StatusCode);
        Assert.AreEqual(status == 200, invoked);
    }

    [DataTestMethod]
    [DataRow("{", -32700)]
    [DataRow("null", -32600)]
    [DataRow("[]", -32600)]
    [DataRow("{}", -32600)]
    [DataRow("{\"jsonrpc\":\"1.0\",\"method\":\"server/discover\"}", -32600)]
    [DataRow("{\"jsonrpc\":\"2.0\",\"method\":42}", -32600)]
    public async Task Invoke_RejectsInvalidJsonRpcBody(string body, int errorCode)
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var context = CreateContext(services);
        context.Request.Headers[McpProtocol.VersionHeader] = McpProtocol.Version;
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;

        await AssertRejectedAsync(context, errorCode);
    }

    [DataTestMethod]
    [DataRow(null, 415)]
    [DataRow("text/plain", 415)]
    [DataRow("application/json; charset=utf-8", 200)]
    public async Task Invoke_RequiresJsonContentType(string? contentType, int status)
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var context = CreateContext(services);
        context.Request.Headers[McpProtocol.VersionHeader] = McpProtocol.Version;
        context.Request.ContentType = contentType;
        var invoked = false;

        await CreateMiddleware(_ => { invoked = true; return Task.CompletedTask; }).InvokeAsync(context);

        Assert.AreEqual(status, context.Response.StatusCode);
        Assert.AreEqual(status == 200, invoked);
    }

    [DataTestMethod]
    [DataRow("/mcp")]
    [DataRow("/adapters/example/mcp")]
    public async Task Invoke_PreservesChunkedBodyForForwarding(string path)
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var context = CreateContext(services);
        context.Request.Path = path;
        context.Request.Headers[McpProtocol.VersionHeader] = McpProtocol.Version;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(CreateMessage());
        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(bytes);
        await pipe.Writer.CompleteAsync();
        context.Request.Body = pipe.Reader.AsStream();
        context.Request.ContentLength = null;
        var invoked = false;

        await CreateMiddleware(async forwardedContext =>
        {
            invoked = true;
            using var request = HttpProxy.CreateProxiedHttpRequest(forwardedContext);
            Assert.IsNull(request.Content!.Headers.ContentLength);
            CollectionAssert.AreEqual(bytes, await request.Content.ReadAsByteArrayAsync());
        }).InvokeAsync(context);

        Assert.IsTrue(invoked);
    }

    private static async Task AssertRejectedAsync(DefaultHttpContext context, int errorCode)
    {
        await CreateMiddleware(_ => throw new AssertFailedException("Request must not reach a backend.")).InvokeAsync(context);
        Assert.AreEqual(400, context.Response.StatusCode);
        using var result = JsonDocument.Parse(((MemoryStream)context.Response.Body).ToArray());
        Assert.AreEqual(errorCode, result.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    private static JsonObject CreateMessage(string method = "server/discover") => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = 1,
        ["method"] = method,
        ["params"] = new JsonObject
        {
            ["_meta"] = new JsonObject
            {
                ["io.modelcontextprotocol/protocolVersion"] = McpProtocol.Version,
                ["io.modelcontextprotocol/clientCapabilities"] = new JsonObject()
            }
        }
    };

    private static void SetBody(DefaultHttpContext context, JsonObject message)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
    }

    private static DefaultHttpContext CreateContext(IServiceProvider services)
    {
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Method = "POST";
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("gateway.local");
        context.Request.Path = "/adapters/example/mcp";
        context.Request.Headers["Mcp-Method"] = "server/discover";
        context.Request.Headers.Accept = "application/json, text/event-stream";
        context.Request.ContentType = "application/json";
        SetBody(context, CreateMessage());
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static McpEndpointMiddleware CreateMiddleware(RequestDelegate next) => new(next,
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["PublicOrigin"] = "https://portal.example/"
        }).Build());
}