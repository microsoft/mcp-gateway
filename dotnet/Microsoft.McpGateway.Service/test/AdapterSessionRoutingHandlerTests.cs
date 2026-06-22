// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.McpGateway.Service.Routing;
using Microsoft.McpGateway.Service.Session;
using Moq;

namespace Microsoft.McpGateway.Service.Tests
{
    [TestClass]
    public class AdapterSessionRoutingHandlerTests
    {
        private const string TestUserId = "user-1";
        private const string TestAdapterName = "adapter-a";

        private readonly Mock<IServiceNodeInfoProvider> _serviceNodeInfoProviderMock;
        private readonly Mock<IAdapterSessionStore> _sessionStoreMock;
        private readonly AdapterSessionRoutingHandler _handler;

        public AdapterSessionRoutingHandlerTests()
        {
            _serviceNodeInfoProviderMock = new Mock<IServiceNodeInfoProvider>();
            _sessionStoreMock = new Mock<IAdapterSessionStore>();
            _handler = new AdapterSessionRoutingHandler(_serviceNodeInfoProviderMock.Object, _sessionStoreMock.Object, NullLogger<AdapterSessionRoutingHandler>.Instance);
        }

        private static DefaultHttpContext CreateAuthenticatedContext(string userId = TestUserId)
        {
            var context = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, userId)],
                    authenticationType: "Test"))
            };
            return context;
        }

        [TestMethod]
        public async Task GetNewSessionTargetAsync_ReturnsTargetAddress()
        {
            var adapterName = "adapter";
            var httpContext = CreateAuthenticatedContext();
            var cancellationToken = CancellationToken.None;
            var nodeAddresses = new Dictionary<string, string>
            {
                { "node1", "http://address1" },
                { "node2", "http://address2" }
            };

            _serviceNodeInfoProviderMock
                .Setup(x => x.GetNodeAddressesAsync(adapterName, cancellationToken))
                .ReturnsAsync(nodeAddresses);

            var result = await _handler.GetNewSessionTargetAsync(adapterName, httpContext, cancellationToken);

            nodeAddresses.Values.Should().Contain(result);
        }

        [TestMethod]
        public async Task GetExistingSessionTargetAsync_WithValidSessionId_ReturnsTargetAddress()
        {
            var httpContext = CreateAuthenticatedContext();
            var sessionId = "abc123";
            var scopedKey = $"{TestUserId}:{TestAdapterName}:{sessionId}";
            var cancellationToken = CancellationToken.None;
            httpContext.Request.QueryString = new QueryString("?session_id=" + sessionId);

            _sessionStoreMock
                .Setup(x => x.TryGetAsync(scopedKey, cancellationToken))
                .ReturnsAsync(("http://target", true));

            var result = await _handler.GetExistingSessionTargetAsync(TestAdapterName, httpContext, cancellationToken);

            result.Should().Be("http://target");
        }

        [TestMethod]
        public async Task GetExistingSessionTargetAsync_WithMissingSessionId_ThrowsArgumentException()
        {
            var httpContext = CreateAuthenticatedContext();
            var cancellationToken = CancellationToken.None;

            Func<Task> act = () => _handler.GetExistingSessionTargetAsync(TestAdapterName, httpContext, cancellationToken);

            await act.Should().ThrowAsync<ArgumentException>()
                .WithMessage("Session id not found in the request.");
        }

        [TestMethod]
        public async Task GetExistingSessionTargetAsync_WithUnknownSessionId_ThrowsArgumentException()
        {
            // "unknown" passes IsValidSessionId; the failure is a cache miss on the scoped key,
            // which represents an expired or never-issued session.
            var httpContext = CreateAuthenticatedContext();
            var sessionId = "unknown";
            var scopedKey = $"{TestUserId}:{TestAdapterName}:{sessionId}";
            var cancellationToken = CancellationToken.None;
            httpContext.Request.QueryString = new QueryString("?session_id=" + sessionId);

            _sessionStoreMock
                .Setup(x => x.TryGetAsync(scopedKey, cancellationToken))
                .ReturnsAsync((null, false));

            Func<Task> act = () => _handler.GetExistingSessionTargetAsync(TestAdapterName, httpContext, cancellationToken);

            await act.Should().ThrowAsync<ArgumentException>()
                .WithMessage("Session id is not valid, or has expired.");
        }

        [DataTestMethod]
        [DataRow("bad:id")]
        [DataRow("bad/id")]
        [DataRow("..")]
        [DataRow("evil@host")]
        [DataRow("has space")]
        public async Task GetExistingSessionTargetAsync_WithMalformedSessionId_FastFailsWithoutCacheLookup(string sessionId)
        {
            var httpContext = CreateAuthenticatedContext();
            var cancellationToken = CancellationToken.None;
            httpContext.Request.Headers["mcp-session-id"] = sessionId;

            Func<Task> act = () => _handler.GetExistingSessionTargetAsync(TestAdapterName, httpContext, cancellationToken);

            await act.Should().ThrowAsync<ArgumentException>()
                .WithMessage("Session id is not valid, or has expired.");

            _sessionStoreMock.Verify(
                x => x.TryGetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [TestMethod]
        public async Task GetExistingSessionTargetAsync_DoesNotLookUpUnscopedKey()
        {
            // A request from User B carrying User A's raw session id must NOT resolve to User A's
            // routing target — the lookup is performed under User B's scoped key.
            var httpContext = CreateAuthenticatedContext("user-B");
            var victimSessionId = "victim-session-id";
            var cancellationToken = CancellationToken.None;
            httpContext.Request.Headers["mcp-session-id"] = victimSessionId;

            // Simulate the victim's mapping still being present under the victim's scoped key.
            _sessionStoreMock
                .Setup(x => x.TryGetAsync($"user-A:{TestAdapterName}:{victimSessionId}", cancellationToken))
                .ReturnsAsync(("http://victim-pod", true));
            _sessionStoreMock
                .Setup(x => x.TryGetAsync($"user-B:{TestAdapterName}:{victimSessionId}", cancellationToken))
                .ReturnsAsync((null, false));

            Func<Task> act = () => _handler.GetExistingSessionTargetAsync(TestAdapterName, httpContext, cancellationToken);

            await act.Should().ThrowAsync<ArgumentException>()
                .WithMessage("Session id is not valid, or has expired.");
            _sessionStoreMock.Verify(x => x.TryGetAsync(victimSessionId, cancellationToken), Times.Never);
        }

        [TestMethod]
        public async Task GetExistingSessionTargetAsync_WithDifferentAdapterRoute_DoesNotResolveSession()
        {
            // SEC-0003: a session established against adapter A must NOT resolve when the same
            // user replays adapter A's session id through adapter B's route, even though the user
            // is still authorized for adapter B. The lookup is scoped to the route adapter, so the
            // entry created under adapter A is never consulted, and the bypass is closed.
            var httpContext = CreateAuthenticatedContext();
            var sessionId = "session-on-a";
            var cancellationToken = CancellationToken.None;
            httpContext.Request.Headers["mcp-session-id"] = sessionId;

            // Adapter A's mapping exists, but the request is being routed through adapter B.
            _sessionStoreMock
                .Setup(x => x.TryGetAsync($"{TestUserId}:adapter-a:{sessionId}", cancellationToken))
                .ReturnsAsync(("http://adapter-a-backend", true));
            _sessionStoreMock
                .Setup(x => x.TryGetAsync($"{TestUserId}:adapter-b:{sessionId}", cancellationToken))
                .ReturnsAsync((null, false));

            Func<Task> act = () => _handler.GetExistingSessionTargetAsync("adapter-b", httpContext, cancellationToken);

            await act.Should().ThrowAsync<ArgumentException>()
                .WithMessage("Session id is not valid, or has expired.");

            // Adapter A's backend mapping must never be consulted for an adapter B request.
            _sessionStoreMock.Verify(
                x => x.TryGetAsync($"{TestUserId}:adapter-a:{sessionId}", cancellationToken),
                Times.Never);
        }

        [TestMethod]
        public async Task GetExistingSessionTargetAsync_WithMatchingAdapterRoute_ResolvesSession()
        {
            // The same session id resolves when replayed through the adapter it was created on.
            var httpContext = CreateAuthenticatedContext();
            var sessionId = "session-on-a";
            var cancellationToken = CancellationToken.None;
            httpContext.Request.Headers["mcp-session-id"] = sessionId;

            _sessionStoreMock
                .Setup(x => x.TryGetAsync($"{TestUserId}:adapter-a:{sessionId}", cancellationToken))
                .ReturnsAsync(("http://adapter-a-backend", true));

            var result = await _handler.GetExistingSessionTargetAsync("adapter-a", httpContext, cancellationToken);

            result.Should().Be("http://adapter-a-backend");
        }

        [TestMethod]
        public async Task GetExistingSessionTargetAsync_WithEmptyAdapterName_Throws()
        {
            var httpContext = CreateAuthenticatedContext();
            httpContext.Request.Headers["mcp-session-id"] = "abc123";

            Func<Task> act = () => _handler.GetExistingSessionTargetAsync(string.Empty, httpContext, CancellationToken.None);

            await act.Should().ThrowAsync<ArgumentException>();
        }

        [TestMethod]
        public async Task GetExistingSessionTargetAsync_WithoutAuthenticatedUser_ThrowsUnauthorized()
        {
            var httpContext = new DefaultHttpContext();
            httpContext.Request.Headers["mcp-session-id"] = "abc123";

            Func<Task> act = () => _handler.GetExistingSessionTargetAsync(TestAdapterName, httpContext, CancellationToken.None);

            await act.Should().ThrowAsync<UnauthorizedAccessException>();
        }

        [TestMethod]
        public void BuildScopedSessionKey_ProducesUserScopedKey()
        {
            var httpContext = CreateAuthenticatedContext("alice");

            var key = AdapterSessionRoutingHandler.BuildScopedSessionKey(httpContext, "adapter-a", "sess-1");

            key.Should().Be("alice:adapter-a:sess-1");
        }

        [TestMethod]
        public void BuildScopedSessionKey_BindsToAdapterName()
        {
            // The same (user, session id) pair under two different adapters must produce two
            // distinct keys so a session can never be resolved through a different adapter route.
            var httpContext = CreateAuthenticatedContext("alice");

            var keyA = AdapterSessionRoutingHandler.BuildScopedSessionKey(httpContext, "adapter-a", "sess-1");
            var keyB = AdapterSessionRoutingHandler.BuildScopedSessionKey(httpContext, "adapter-b", "sess-1");

            keyA.Should().NotBe(keyB);
            keyA.Should().Be("alice:adapter-a:sess-1");
            keyB.Should().Be("alice:adapter-b:sess-1");
        }

        [TestMethod]
        public void BuildScopedSessionKey_WithoutAdapterName_Throws()
        {
            var httpContext = CreateAuthenticatedContext("alice");

            Action act = () => AdapterSessionRoutingHandler.BuildScopedSessionKey(httpContext, string.Empty, "sess-1");

            act.Should().Throw<ArgumentException>();
        }

        [TestMethod]
        public void BuildScopedSessionKey_WithoutUserId_Throws()
        {
            var httpContext = new DefaultHttpContext();

            Action act = () => AdapterSessionRoutingHandler.BuildScopedSessionKey(httpContext, "adapter-a", "sess-1");

            act.Should().Throw<UnauthorizedAccessException>();
        }

        [DataTestMethod]
        [DataRow("abc123")]
        [DataRow("550e8400-e29b-41d4-a716-446655440000")]
        [DataRow("A-B-C-1-2-3")]
        public void IsValidSessionId_AcceptsWellFormedValues(string sessionId)
        {
            AdapterSessionRoutingHandler.IsValidSessionId(sessionId).Should().BeTrue();
        }

        [DataTestMethod]
        [DataRow("")]
        [DataRow("contains space")]
        [DataRow("with:colon")]
        [DataRow("with/slash")]
        [DataRow("with..dot")]
        [DataRow("with@at")]
        public void IsValidSessionId_RejectsUnsafeValues(string sessionId)
        {
            AdapterSessionRoutingHandler.IsValidSessionId(sessionId).Should().BeFalse();
        }

        [TestMethod]
        public void IsValidSessionId_RejectsNull()
        {
            AdapterSessionRoutingHandler.IsValidSessionId(null).Should().BeFalse();
        }

        [TestMethod]
        public void IsValidSessionId_RejectsOverlyLongValues()
        {
            var tooLong = new string('a', 129);

            AdapterSessionRoutingHandler.IsValidSessionId(tooLong).Should().BeFalse();
        }
    }
}
