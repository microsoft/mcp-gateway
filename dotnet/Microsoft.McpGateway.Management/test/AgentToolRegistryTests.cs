// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.McpGateway.Management.Authorization;
using Microsoft.McpGateway.Management.Contracts;
using Microsoft.McpGateway.Management.Foundry;
using Microsoft.McpGateway.Management.Store;
using ModelContextProtocol.Protocol;
using Moq;

namespace Microsoft.McpGateway.Management.Tests
{
    /// <summary>
    /// Regression tests for MSRC-122743: nested <c>agent:</c> and <c>mcp:</c>
    /// references must be re-authorized against the effective caller's current
    /// roles at resolve time and again at invocation time, so a stale parent
    /// reference cannot execute a since-restricted child resource.
    /// </summary>
    [TestClass]
    public class AgentToolRegistryTests
    {
        private readonly Mock<IToolResourceStore> _toolStore = new();
        private readonly Mock<IAgentResourceStore> _agentStore = new();
        private readonly Mock<ISessionResourceStore> _sessionStore = new();
        private readonly Mock<IHttpClientFactory> _httpFactory = new();
        private readonly Mock<IPermissionProvider> _permission = new();
        private readonly Mock<IBuiltinToolAuthorizer> _builtinAuthorizer = new();
        private readonly Mock<ILogger<AgentToolRegistry>> _logger = new();
        private readonly SubAgentInvoker _subAgentInvoker;
        private readonly AgentToolRegistry _registry;
        private int _runnerFactoryCalls;

        public AgentToolRegistryTests()
        {
            // A real SubAgentInvoker so the agent: branch is reachable, but its
            // runner factory records invocation and yields null — it must never
            // be reached when authorization denies the nested call.
            _subAgentInvoker = new SubAgentInvoker(
                _sessionStore.Object,
                () => { _runnerFactoryCalls++; return null!; },
                Mock.Of<ILogger<SubAgentInvoker>>());

            _registry = new AgentToolRegistry(
                _toolStore.Object,
                _agentStore.Object,
                _httpFactory.Object,
                _permission.Object,
                _builtinAuthorizer.Object,
                _logger.Object,
                _subAgentInvoker,
                builtinExecutor: null);
        }

        // Build a registry wired with a real BuiltinToolExecutor and a builtin
        // authorizer that returns the given decision, for exercising the
        // built-in capability gates.
        private AgentToolRegistry CreateBuiltinRegistry(bool authorized)
        {
            var authorizer = new Mock<IBuiltinToolAuthorizer>();
            authorizer.Setup(a => a.IsAuthorized(It.IsAny<ClaimsPrincipal>())).Returns(authorized);
            return new AgentToolRegistry(
                _toolStore.Object,
                _agentStore.Object,
                _httpFactory.Object,
                _permission.Object,
                authorizer.Object,
                _logger.Object,
                _subAgentInvoker,
                new BuiltinToolExecutor(Mock.Of<ILogger<BuiltinToolExecutor>>()));
        }

        private static ClaimsPrincipal Caller(string userId, params string[] roles)
        {
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };
            claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
            return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        }

        private static ToolResource CreateTool(string name, string creator, params string[] requiredRoles)
        {
            var schema = JsonSerializer.Deserialize<JsonElement>(
                JsonSerializer.Serialize(new { type = "object", properties = new { } }));
            var data = new ToolData
            {
                Name = name,
                ImageName = "img",
                ImageVersion = "v1",
                ReplicaCount = 1,
                EnvironmentVariables = [],
                ToolDefinition = new ToolDefinition
                {
                    Tool = new Tool { Name = name, Description = "d", InputSchema = schema },
                    Port = 443,
                    Path = "/score",
                },
                RequiredRoles = requiredRoles.ToList(),
            };
            return ToolResource.Create(data, creator, DateTimeOffset.UtcNow);
        }

        private static AgentResource CreateAgent(string name, string creator, params string[] requiredRoles)
        {
            var data = new AgentData
            {
                Name = name,
                Model = "gpt-4o",
                System = "sys",
                Tools = [],
                RequiredRoles = requiredRoles.ToList(),
            };
            return AgentResource.Create(data, creator, DateTimeOffset.UtcNow);
        }

        private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
        {
            public int Calls { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Calls++;
                return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
            }
        }

        // ---- ResolveAsync: unauthorized nested resources must not be advertised ----

        [TestMethod]
        public async Task ResolveAsync_ShouldExcludeMcpTool_WhenCallerLacksPermission()
        {
            var tool = CreateTool("finance-ledger-tool", "admin-poc", "finance-admin");
            _toolStore.Setup(s => s.TryGetAsync("finance-ledger-tool", It.IsAny<CancellationToken>())).ReturnsAsync(tool);
            _permission.Setup(p => p.CheckAccessAsync(It.IsAny<ClaimsPrincipal>(), tool, Operation.Read)).ReturnsAsync(false);

            var resolved = await _registry.ResolveAsync(["mcp:finance-ledger-tool"], Caller("ordinary-poc", "ordinary-user"), CancellationToken.None);

            resolved.Should().BeEmpty();
        }

        [TestMethod]
        public async Task ResolveAsync_ShouldIncludeMcpTool_WhenCallerHasPermission()
        {
            var tool = CreateTool("finance-ledger-tool", "admin-poc", "finance-admin");
            _toolStore.Setup(s => s.TryGetAsync("finance-ledger-tool", It.IsAny<CancellationToken>())).ReturnsAsync(tool);
            _permission.Setup(p => p.CheckAccessAsync(It.IsAny<ClaimsPrincipal>(), tool, Operation.Read)).ReturnsAsync(true);

            var resolved = await _registry.ResolveAsync(["mcp:finance-ledger-tool"], Caller("admin-poc", "finance-admin"), CancellationToken.None);

            resolved.Should().ContainSingle().Which.Should().BeOfType<McpResolvedTool>();
        }

        [TestMethod]
        public async Task ResolveAsync_ShouldExcludeSubagent_WhenCallerLacksPermission()
        {
            var child = CreateAgent("finance-ledger-child-agent", "admin-poc", "finance-admin");
            _agentStore.Setup(s => s.TryGetAsync("finance-ledger-child-agent", It.IsAny<CancellationToken>())).ReturnsAsync(child);
            _permission.Setup(p => p.CheckAccessAsync(It.IsAny<ClaimsPrincipal>(), child, Operation.Read)).ReturnsAsync(false);

            var resolved = await _registry.ResolveAsync(["agent:finance-ledger-child-agent"], Caller("ordinary-poc", "ordinary-user"), CancellationToken.None);

            resolved.Should().BeEmpty();
            _runnerFactoryCalls.Should().Be(0);
        }

        // ---- ExecuteAsync: invocation-time gate against current policy ----

        [TestMethod]
        public async Task ExecuteAsync_ShouldDenyMcpTool_WhenCallerLacksPermission()
        {
            var tool = CreateTool("finance-ledger-tool", "admin-poc", "finance-admin");
            _toolStore.Setup(s => s.TryGetAsync("finance-ledger-tool", It.IsAny<CancellationToken>())).ReturnsAsync(tool);
            _permission.Setup(p => p.CheckAccessAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<IManagedResource>(), Operation.Read)).ReturnsAsync(false);

            var result = await _registry.ExecuteAsync(
                new McpResolvedTool("finance-ledger-tool", tool),
                "{}",
                parentSessionId: "parent-session",
                workingDirectory: null,
                Caller("ordinary-poc", "ordinary-user"),
                CancellationToken.None);

            result.IsError.Should().BeTrue();
            result.Content.Should().Contain("permission");
            _httpFactory.Verify(f => f.CreateClient(It.IsAny<string>()), Times.Never);
        }

        [TestMethod]
        public async Task ExecuteAsync_ShouldInvokeMcpTool_WhenCallerHasPermission()
        {
            var tool = CreateTool("finance-ledger-tool", "admin-poc", "finance-admin");
            _toolStore.Setup(s => s.TryGetAsync("finance-ledger-tool", It.IsAny<CancellationToken>())).ReturnsAsync(tool);
            _permission.Setup(p => p.CheckAccessAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<IManagedResource>(), Operation.Read)).ReturnsAsync(true);
            var handler = new StubHandler(HttpStatusCode.OK, "{\"ok\":true}");
            _httpFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient(handler));

            var result = await _registry.ExecuteAsync(
                new McpResolvedTool("finance-ledger-tool", tool),
                "{}",
                parentSessionId: "parent-session",
                workingDirectory: null,
                Caller("admin-poc", "finance-admin"),
                CancellationToken.None);

            result.IsError.Should().BeFalse();
            result.Content.Should().Contain("ok");
            handler.Calls.Should().Be(1);
        }

        [TestMethod]
        public async Task ExecuteAsync_ShouldDenyMcpTool_WhenToolNoLongerExists()
        {
            var tool = CreateTool("finance-ledger-tool", "admin-poc", "finance-admin");
            _toolStore.Setup(s => s.TryGetAsync("finance-ledger-tool", It.IsAny<CancellationToken>())).ReturnsAsync((ToolResource?)null);

            var result = await _registry.ExecuteAsync(
                new McpResolvedTool("finance-ledger-tool", tool),
                "{}",
                parentSessionId: "parent-session",
                workingDirectory: null,
                Caller("ordinary-poc", "ordinary-user"),
                CancellationToken.None);

            result.IsError.Should().BeTrue();
            _httpFactory.Verify(f => f.CreateClient(It.IsAny<string>()), Times.Never);
        }

        [TestMethod]
        public async Task ExecuteAsync_ShouldDenySubagent_WhenCallerLacksPermission()
        {
            var child = CreateAgent("finance-ledger-child-agent", "admin-poc", "finance-admin");
            _agentStore.Setup(s => s.TryGetAsync("finance-ledger-child-agent", It.IsAny<CancellationToken>())).ReturnsAsync(child);
            _permission.Setup(p => p.CheckAccessAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<IManagedResource>(), Operation.Read)).ReturnsAsync(false);

            var result = await _registry.ExecuteAsync(
                new SubAgentResolvedTool("agent_finance-ledger-child-agent", child),
                "{\"input\":\"transfer funds\"}",
                parentSessionId: "parent-session",
                workingDirectory: null,
                Caller("ordinary-poc", "ordinary-user"),
                CancellationToken.None);

            result.IsError.Should().BeTrue();
            result.Content.Should().Contain("permission");
            // The child run must never start: no session persisted, no runner created.
            _runnerFactoryCalls.Should().Be(0);
            _sessionStore.Verify(s => s.UpsertAsync(It.IsAny<SessionResource>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        // ---- Built-in tools: privileged capability gate ----

        [TestMethod]
        public async Task ResolveAsync_ShouldExcludeBuiltin_WhenCallerNotAuthorized()
        {
            var registry = CreateBuiltinRegistry(authorized: false);

            var resolved = await registry.ResolveAsync(["builtin:bash"], Caller("ordinary-poc", "ordinary-user"), CancellationToken.None);

            resolved.Should().BeEmpty();
        }

        [TestMethod]
        public async Task ResolveAsync_ShouldIncludeBuiltin_WhenCallerAuthorized()
        {
            var registry = CreateBuiltinRegistry(authorized: true);

            var resolved = await registry.ResolveAsync(["builtin:bash"], Caller("admin-poc", "mcp.admin"), CancellationToken.None);

            resolved.Should().ContainSingle()
                .Which.Should().BeOfType<BuiltinResolvedTool>()
                .Which.Name.Should().Be(BuiltinToolExecutor.Bash);
        }

        [TestMethod]
        public async Task ExecuteAsync_ShouldDenyBuiltin_WhenCallerNotAuthorized()
        {
            // A real BuiltinToolExecutor is wired up, so reaching it would spawn
            // bash; the authorization gate must short-circuit before that.
            var registry = CreateBuiltinRegistry(authorized: false);

            var result = await registry.ExecuteAsync(
                new BuiltinResolvedTool(BuiltinToolExecutor.Bash),
                "{\"command\":\"echo hi\"}",
                parentSessionId: "parent-session",
                workingDirectory: "/tmp/session",
                Caller("ordinary-poc", "ordinary-user"),
                CancellationToken.None);

            result.IsError.Should().BeTrue();
            result.Content.Should().Contain("permission");
        }
    }
}
