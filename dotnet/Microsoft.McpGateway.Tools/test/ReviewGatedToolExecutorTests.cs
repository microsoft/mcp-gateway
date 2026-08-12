// Copyright (c) invinoveritas.
// Offline unit tests for ReviewGatedToolExecutor -- HTTP is mocked here (same discipline as
// mcp-gateway's own StorageToolDefinitionProviderTests.cs: MSTest + Moq, no network). A
// SEPARATE live test (ReviewGatedToolExecutorLiveTests.cs) exercises the real production
// invinoveritas API and is not run as part of the normal offline suite.

using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.McpGateway.Tools.Contracts;
using Microsoft.McpGateway.Tools.Services;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Moq;

namespace Microsoft.McpGateway.Tools.Tests
{
    /// <summary>
    /// Minimal fake <see cref="HttpMessageHandler"/> -- avoids pulling in Moq.Protected (not
    /// already a dependency of this repo's central package management) just to stub one HTTP
    /// call. Records the request it saw so tests can assert on it if needed.
    /// </summary>
    internal sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond;

        public FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        {
            this.respond = respond;
        }

        public static FakeHandler Returning(HttpStatusCode status, object? body) => new((_, _) =>
        {
            var response = new HttpResponseMessage(status)
            {
                Content = body == null
                    ? new StringContent(string.Empty)
                    : new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        });

        public static FakeHandler NeverResponds() => new(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        });

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => this.respond(request, cancellationToken);
    }

    [TestClass]
    public class ReviewGatedToolExecutorTests
    {
        private static RequestContext<CallToolRequestParams> BuildRequest(string toolName, object? arguments = null)
        {
            var argsJson = JsonSerializer.Serialize(arguments ?? new { });
            var argsElement = JsonSerializer.Deserialize<JsonElement>(argsJson);
            var argsDict = argsElement.ValueKind == JsonValueKind.Object
                ? argsElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value)
                : new Dictionary<string, JsonElement>();

            // McpServer is abstract with a protected ctor; RequestContext<T> requires a
            // non-null instance even though ReviewGatedToolExecutor never touches it. A bare
            // Moq proxy satisfies the non-null check without needing a real transport/session.
            return new RequestContext<CallToolRequestParams>(
                server: new Mock<McpServer>().Object,
                jsonRpcRequest: new JsonRpcRequest { Method = "tools/call" })
            {
                Params = new CallToolRequestParams
                {
                    Name = toolName,
                    Arguments = argsDict,
                },
            };
        }

        private static HttpClient MockReviewClient(HttpStatusCode status, object? body)
            => new(FakeHandler.Returning(status, body)) { BaseAddress = new Uri("https://api.babyblueviper.com") };

        [TestMethod]
        public async Task RejectVerdict_AboveThreshold_BlocksAndNeverCallsInner()
        {
            var inner = new Mock<IToolExecutor>(MockBehavior.Strict); // Strict: any call fails the test.
            var client = MockReviewClient(HttpStatusCode.OK, new { verdict = "reject", confidence = 1.0, summary = "destroys the host" });
            var gate = new ReviewGatedToolExecutor(inner.Object, client, Mock.Of<ILogger<ReviewGatedToolExecutor>>());

            var result = await gate.ExecuteToolAsync(BuildRequest("run_shell", new { command = "rm -rf / --no-preserve-root" }), CancellationToken.None);

            Assert.IsTrue(result.IsError);
            StringAssert.Contains(((TextContentBlock)result.Content[0]).Text, "Blocked by invinoveritas");
            inner.VerifyNoOtherCalls();
        }

        [TestMethod]
        public async Task ApproveVerdict_FallsThroughToInnerExecutor()
        {
            var expected = new CallToolResult { Content = [new TextContentBlock { Text = "ok" }] };
            var inner = new Mock<IToolExecutor>();
            inner.Setup(x => x.ExecuteToolAsync(It.IsAny<RequestContext<CallToolRequestParams>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(expected);
            var client = MockReviewClient(HttpStatusCode.OK, new { verdict = "approve", confidence = 0.95, summary = "benign" });
            var gate = new ReviewGatedToolExecutor(inner.Object, client, Mock.Of<ILogger<ReviewGatedToolExecutor>>());

            var result = await gate.ExecuteToolAsync(BuildRequest("list_files", new { path = "/tmp" }), CancellationToken.None);

            Assert.AreSame(expected, result);
            inner.Verify(x => x.ExecuteToolAsync(It.IsAny<RequestContext<CallToolRequestParams>>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task RejectVerdict_BelowConfidenceThreshold_FallsThrough()
        {
            var expected = new CallToolResult { Content = [new TextContentBlock { Text = "ok" }] };
            var inner = new Mock<IToolExecutor>();
            inner.Setup(x => x.ExecuteToolAsync(It.IsAny<RequestContext<CallToolRequestParams>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(expected);
            // reject, but confidence 0.4 is below the default 0.7 threshold -- genuine
            // uncertainty, not a clean call; falls through per the fail-open-on-uncertainty design.
            var client = MockReviewClient(HttpStatusCode.OK, new { verdict = "reject", confidence = 0.4, summary = "ambiguous" });
            var gate = new ReviewGatedToolExecutor(inner.Object, client, Mock.Of<ILogger<ReviewGatedToolExecutor>>());

            var result = await gate.ExecuteToolAsync(BuildRequest("some_tool"), CancellationToken.None);

            Assert.AreSame(expected, result);
        }

        [TestMethod]
        public async Task GateUnavailable_502_FallsThroughFailOpen()
        {
            var expected = new CallToolResult { Content = [new TextContentBlock { Text = "ok" }] };
            var inner = new Mock<IToolExecutor>();
            inner.Setup(x => x.ExecuteToolAsync(It.IsAny<RequestContext<CallToolRequestParams>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(expected);
            var client = MockReviewClient(HttpStatusCode.BadGateway, null);
            var gate = new ReviewGatedToolExecutor(inner.Object, client, Mock.Of<ILogger<ReviewGatedToolExecutor>>());

            var result = await gate.ExecuteToolAsync(BuildRequest("some_tool"), CancellationToken.None);

            Assert.AreSame(expected, result);
        }

        [TestMethod]
        public async Task GateTimesOut_FallsThroughFailOpen()
        {
            var expected = new CallToolResult { Content = [new TextContentBlock { Text = "ok" }] };
            var inner = new Mock<IToolExecutor>();
            inner.Setup(x => x.ExecuteToolAsync(It.IsAny<RequestContext<CallToolRequestParams>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(expected);

            var client = new HttpClient(FakeHandler.NeverResponds()) { BaseAddress = new Uri("https://api.babyblueviper.com") };
            var gate = new ReviewGatedToolExecutor(inner.Object, client, Mock.Of<ILogger<ReviewGatedToolExecutor>>(), new ReviewGateOptions { Timeout = TimeSpan.FromMilliseconds(50) });

            var result = await gate.ExecuteToolAsync(BuildRequest("some_tool"), CancellationToken.None);

            Assert.AreSame(expected, result);
        }
    }
}
