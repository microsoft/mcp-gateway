// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Security.Claims;
using System.Text;
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

        [TestMethod]
        public async Task CreateProxiedHttpRequest_WithRequestBody_CanBeReadTwice()
        {
            var body = """{"jsonrpc":"2.0","method":"tools/list"}""";
            var bodyBytes = Encoding.UTF8.GetBytes(body);

            var context = CreateAuthenticatedContext("/adapters/test/mcp");
            context.Request.ContentLength = bodyBytes.Length;
            context.Request.Body = new MemoryStream(bodyBytes);

            var request = HttpProxy.CreateProxiedHttpRequest(context);

            using var firstCopy = new MemoryStream();
            await request.Content!.CopyToAsync(firstCopy);

            using var secondCopy = new MemoryStream();
            await request.Content.CopyToAsync(secondCopy);

            Encoding.UTF8.GetString(secondCopy.ToArray())
                .Should()
                .Be(body);
        }

        [TestMethod]
        public async Task CreateProxiedHttpRequest_WithNonSeekableBody_CanBeReadTwice()
        {
            var body = """{"jsonrpc":"2.0","method":"tools/list"}""";
            var bodyBytes = Encoding.UTF8.GetBytes(body);

            var context = CreateAuthenticatedContext("/adapters/test/mcp");
            context.Request.ContentLength = bodyBytes.Length;
            context.Request.Body = new NonSeekableStream(bodyBytes);

            var request = HttpProxy.CreateProxiedHttpRequest(context);

            using var firstCopy = new MemoryStream();
            await request.Content!.CopyToAsync(firstCopy);

            using var secondCopy = new MemoryStream();
            await request.Content.CopyToAsync(secondCopy);
        }

        private sealed class NonSeekableStream(byte[] data) : Stream
        {
            private readonly MemoryStream inner = new(data);

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => inner.Length;

            public override long Position
            {
                get => inner.Position;
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count)
                => inner.Read(buffer, offset, count);

            public override ValueTask<int> ReadAsync(
                Memory<byte> buffer,
                CancellationToken cancellationToken = default)
                => inner.ReadAsync(buffer, cancellationToken);

            public override long Seek(long offset, SeekOrigin origin)
                => throw new NotSupportedException();

            public override void SetLength(long value)
                => throw new NotSupportedException();

            public override void Flush()
                => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count)
                => throw new NotSupportedException();
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