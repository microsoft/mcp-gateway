// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.McpGateway.Management.Authorization;

namespace Microsoft.McpGateway.Service.Tests
{
    [TestClass]
    public class HttpProxyTests
    {
        private const string GatewaySecret = "the-shared-secret";

        [TestMethod]
        public void CreateProxiedHttpRequest_WithAdapterTarget_DoesNotForwardGatewaySecret()
        {
            var context = CreateAuthenticatedContext("/adapters/victim/mcp");

            var request = HttpProxy.CreateProxiedHttpRequest(context);

            request.Headers.Contains(ForwardedIdentityHeaders.GatewaySecret).Should().BeFalse();
        }

        [TestMethod]
        public void CreateProxiedHttpRequest_WithToolGatewayTarget_ForwardsGatewaySecret()
        {
            var context = CreateAuthenticatedContext("/mcp");

            var request = HttpProxy.CreateProxiedHttpRequest(context, forwardGatewaySecret: true);

            request.Headers.GetValues(ForwardedIdentityHeaders.GatewaySecret).Single().Should().Be(GatewaySecret);
        }

        [TestMethod]
        public void CreateProxiedHttpRequest_WithAdapterTarget_StillForwardsIdentityHeaders()
        {
            var context = CreateAuthenticatedContext("/adapters/victim/mcp");

            var request = HttpProxy.CreateProxiedHttpRequest(context);

            request.Headers.GetValues(ForwardedIdentityHeaders.UserId).Single().Should().Be("user-1");
            request.Headers.GetValues(ForwardedIdentityHeaders.Roles).Single().Should().Be("mcp.admin");
        }

        [TestMethod]
        public void CreateProxiedHttpRequest_WithClientSuppliedGatewaySecret_StripsItFromAdapterRequest()
        {
            var context = CreateAuthenticatedContext("/adapters/victim/mcp");
            context.Request.Headers[ForwardedIdentityHeaders.GatewaySecret] = "attacker-supplied";

            var request = HttpProxy.CreateProxiedHttpRequest(context);

            request.Headers.Contains(ForwardedIdentityHeaders.GatewaySecret).Should().BeFalse();
        }

        private static DefaultHttpContext CreateAuthenticatedContext(string path)
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["GatewaySettings:Secret"] = GatewaySecret })
                .Build();

            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(configuration);

            var context = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, "user-1"), new Claim(ClaimTypes.Role, "mcp.admin")],
                    "Test")),
                RequestServices = services.BuildServiceProvider()
            };

            context.Request.Method = "POST";
            context.Request.Scheme = "http";
            context.Request.Host = new HostString("gateway.local");
            context.Request.Path = path;
            context.Request.ContentLength = 0;

            return context;
        }
    }
}
