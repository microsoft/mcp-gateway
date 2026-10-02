using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.McpGateway.Management.Authorization;
using Microsoft.McpGateway.Management.Store;
using Microsoft.McpGateway.Service.Controllers;
using Microsoft.McpGateway.Service.Routing;
using Moq;

namespace Microsoft.McpGateway.Service.Tests;

[TestClass]
public class AdapterReverseProxyControllerTests
{
    [DataTestMethod]
    [DataRow(200)]
    [DataRow(400)]
    [DataRow(404)]
    [DataRow(405)]
    public async Task Forward_PreservesBackendResponseWithoutSessionRouting(int statusCode)
    {
        const string body = "{\"jsonrpc\":\"2.0\",\"id\":1,\"error\":{\"code\":-32601,\"message\":\"Unknown method\"}}";
        using var handler = new StubHandler(statusCode, body);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(value => value.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler, false));
        var nodes = new Mock<IServiceNodeInfoProvider>(MockBehavior.Strict);
        nodes.Setup(value => value.GetNodeAddressesAsync("toolgateway", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string> { ["pod-0"] = "http://backend:8000" });
        using var services = new ServiceCollection().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Method = "POST";
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("gateway");
        context.Request.Path = "/mcp";
        context.Request.Headers["MCP-Protocol-Version"] = "2026-07-28";
        context.Request.Headers["Mcp-Method"] = "server/discover";
        context.Request.Headers["Mcp-Session-Id"] = "nonexistent:session";
        context.Request.QueryString = new QueryString("?session_id=also-nonexistent&application=value");
        context.Response.Body = new MemoryStream();
        var controller = new AdapterReverseProxyController(factory.Object, nodes.Object,
            Mock.Of<IAdapterResourceStore>(), Mock.Of<IPermissionProvider>(),
            NullLogger<AdapterReverseProxyController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };

        await controller.ForwardStreamableHttpRequest(null, CancellationToken.None);

        Assert.AreEqual(statusCode, context.Response.StatusCode);
        Assert.AreEqual(body, Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray()));
        Assert.AreEqual(1, handler.RequestCount);
        Assert.AreEqual("?application=value", handler.RequestQuery);
        Assert.IsFalse(context.Response.Headers.ContainsKey("Mcp-Session-Id"));
        nodes.VerifyAll();
    }

    private sealed class StubHandler(int statusCode, string body) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public string? RequestQuery { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            RequestQuery = request.RequestUri?.Query;
            var response = new HttpResponseMessage((HttpStatusCode)statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            response.Headers.TryAddWithoutValidation("Mcp-Session-Id", "backend-session");
            return Task.FromResult(response);
        }
    }
}