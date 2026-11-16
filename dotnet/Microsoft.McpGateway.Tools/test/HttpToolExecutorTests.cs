// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using k8s.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.McpGateway.Management.Authorization;
using Microsoft.McpGateway.Management.Contracts;
using Microsoft.McpGateway.Management.Deployment;
using Microsoft.McpGateway.Management.Store;
using Microsoft.McpGateway.Tools.Contracts;
using Microsoft.McpGateway.Tools.Services;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Moq;

namespace Microsoft.McpGateway.Tools.Tests
{
    /// <summary>
    /// The tool gateway must not follow a redirect returned by a tenant tool pod.
    /// </summary>
    [TestClass]
    public class HttpToolExecutorTests
    {
        [DataTestMethod]
        [DataRow(HttpStatusCode.Found)]
        [DataRow(HttpStatusCode.TemporaryRedirect)]
        public async Task ExecuteToolAsync_DoesNotFollowRedirectFromToolPod(HttpStatusCode redirectStatus)
        {
            var internalRequests = 0;
            await using var pod = await StartRedirectingToolPodAsync(redirectStatus, () => Interlocked.Increment(ref internalRequests));
            var services = new ServiceCollection();
            services.AddToolExecutionHttpClient();
            services.ConfigureHttpClientDefaults(client => client.AddHttpMessageHandler(() => new RouteToPodHandler(new Uri(pod.Urls.Single()))));
            using var clients = services.BuildServiceProvider();
            var tool = CreateTool("redirecting-tool");
            var executor = CreateExecutor(clients.GetRequiredService<IHttpClientFactory>(), tool);

            var result = await executor.ExecuteToolAsync(CreateCallToolContext(tool.Name), CancellationToken.None);

            result.IsError.Should().BeTrue();
            result.Content.OfType<TextContentBlock>().Single().Text.Should().NotContain("internal-secret");
            internalRequests.Should().Be(0);
        }

        // Stand-in for a tenant tool pod that answers every invocation with a
        // redirect to another endpoint the gateway can reach.
        private static async Task<WebApplication> StartRedirectingToolPodAsync(HttpStatusCode redirectStatus, Action onInternalRequest)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            var pod = builder.Build();
            pod.MapPost("/score", context =>
            {
                context.Response.StatusCode = (int)redirectStatus;
                context.Response.Headers.Location = "/internal";
                return Task.CompletedTask;
            });
            pod.Map("/internal", () =>
            {
                onInternalRequest();
                return "internal-secret";
            });
            await pod.StartAsync();
            return pod;
        }

        // Sends the cluster-DNS tool endpoint to the local stand-in pod. It runs
        // ahead of the primary handler, which alone decides whether to follow redirects.
        private sealed class RouteToPodHandler(Uri pod) : DelegatingHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                request.RequestUri = new UriBuilder(request.RequestUri!) { Host = pod.Host, Port = pod.Port }.Uri;
                return base.SendAsync(request, cancellationToken);
            }
        }

        private static ToolResource CreateTool(string name)
        {
            var data = new ToolData
            {
                Name = name,
                ImageName = "img",
                ImageVersion = "v1",
                ToolDefinition = new ToolDefinition
                {
                    Tool = new Tool
                    {
                        Name = name,
                        Description = "d",
                        InputSchema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { } }),
                    },
                    Port = 8000,
                    Path = "/score",
                },
            };
            return ToolResource.Create(data, "tool-owner", DateTimeOffset.UtcNow);
        }

        private static HttpToolExecutor CreateExecutor(IHttpClientFactory httpClientFactory, ToolResource tool)
        {
            var toolStore = new Mock<IToolResourceStore>();
            toolStore.Setup(store => store.TryGetAsync(tool.Name, It.IsAny<CancellationToken>())).ReturnsAsync(tool);
            var permissions = new Mock<IPermissionProvider>();
            permissions.Setup(permission => permission.CheckAccessAsync(It.IsAny<ClaimsPrincipal>(), tool, Operation.Read)).ReturnsAsync(true);
            var httpContextAccessor = new Mock<IHttpContextAccessor>();
            httpContextAccessor.Setup(accessor => accessor.HttpContext).Returns(new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "tool-owner")], "test")),
            });

            var labels = new Dictionary<string, string> { ["adapter/type"] = "tool", ["adapter/name"] = tool.Name };
            var kubeClient = new Mock<IKubeClientWrapper>();
            kubeClient.Setup(client => client.ReadStatefulSetAsync(tool.Name, "adapter", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new V1StatefulSet { Metadata = new V1ObjectMeta { Uid = "workload-uid" }, Spec = new V1StatefulSetSpec { Selector = new V1LabelSelector { MatchLabels = labels } } });
            kubeClient.Setup(client => client.ReadServiceAsync($"{tool.Name}-service", "adapter", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new V1Service { Metadata = new V1ObjectMeta { Uid = "service-uid" }, Spec = new V1ServiceSpec { Selector = labels } });

            return new HttpToolExecutor(
                httpClientFactory,
                Mock.Of<IToolDefinitionProvider>(),
                toolStore.Object,
                permissions.Object,
                httpContextAccessor.Object,
                Mock.Of<ILogger<HttpToolExecutor>>(),
                new DeploymentOwnership(kubeClient.Object));
        }

        private static RequestContext<CallToolRequestParams> CreateCallToolContext(string toolName) => new(
            new Mock<McpServer>().Object,
            new JsonRpcRequest { Id = new RequestId(1), Method = "tools/call" },
            new CallToolRequestParams
            {
                Name = toolName,
                Arguments = new Dictionary<string, JsonElement> { ["query"] = JsonSerializer.SerializeToElement("caller-argument") },
            });
    }
}
