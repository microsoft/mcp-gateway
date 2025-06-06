// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.McpGateway.Service.Session;

namespace Microsoft.McpGateway.Service.Controllers
{
    [ApiController]
    [Route("adapters")]
    [Authorize]
    public class AdapterReverseProxyController(IHttpClientFactory httpClientFactory, IAdapterSessionStore sessionStore, ISessionRoutingHandler sessionRoutingHandler) : ControllerBase
    {
        private const string MCPSSEMarker = "/messages/?session_id=";
        private readonly IHttpClientFactory httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        private readonly IAdapterSessionStore sessionStore = sessionStore ?? throw new ArgumentNullException(nameof(sessionStore));
        private readonly ISessionRoutingHandler sessionRoutingHandler = sessionRoutingHandler ?? throw new ArgumentNullException(nameof(sessionRoutingHandler));

        /// <summary>
        /// Support for MCP streamable HTTP connection.
        /// </summary>
        [HttpPost("{name}/mcp")]
        public async Task ForwardStreamableHttpRequest(string name, CancellationToken cancellationToken)
        {
            var sessionId = AdapterSessionRoutingHandler.GetSessionId(HttpContext);
            string? targetAddress;
            if (string.IsNullOrEmpty(sessionId))
                targetAddress = await sessionRoutingHandler.GetNewSessionTargetAsync(name, HttpContext, cancellationToken).ConfigureAwait(false);
            else
                targetAddress = await sessionRoutingHandler.GetExistingSessionTargetAsync(HttpContext, cancellationToken).ConfigureAwait(false);

            if (targetAddress == null)
            {
                HttpContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }

            var proxiedRequest = HttpProxy.CreateProxiedHttpRequest(HttpContext, (uri) => ReplaceUriAddress(uri, targetAddress));

            using var client = httpClientFactory.CreateClient(Constants.HttpClientNames.AdapterProxyClient);
            var response = await client.SendAsync(proxiedRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrEmpty(sessionId))
            {
                sessionId = AdapterSessionRoutingHandler.GetSessionId(response);
                if (!string.IsNullOrEmpty(sessionId))
                    await sessionStore.SetAsync(sessionId, targetAddress, cancellationToken).ConfigureAwait(false);
            }

            await HttpProxy.CopyProxiedHttpResponseAsync(HttpContext, response, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Support for the legacy MCP SSE connection.
        /// </summary>
        [HttpPost("{name}/messages")]
        public async Task ForwardRequest(CancellationToken cancellationToken)
        {
            var targetAddress = await sessionRoutingHandler.GetExistingSessionTargetAsync(HttpContext, cancellationToken).ConfigureAwait(false);

            if (targetAddress == null)
            {
                HttpContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }

            using var client = httpClientFactory.CreateClient(Constants.HttpClientNames.AdapterProxyClient);
            var proxiedRequest = HttpProxy.CreateProxiedHttpRequest(HttpContext, (uri) => ReplaceUriAddress(uri, targetAddress));

            var response = await client.SendAsync(proxiedRequest, cancellationToken).ConfigureAwait(false);

            await HttpProxy.CopyProxiedHttpResponseAsync(HttpContext, response, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Support for the legacy MCP SSE connection.
        /// </summary>
        [HttpGet("{name}/sse")]
        public async Task ForwardSseRequest(string name, CancellationToken cancellationToken)
        {
            var targetAddress = await sessionRoutingHandler.GetNewSessionTargetAsync(name, HttpContext, cancellationToken).ConfigureAwait(false);

            if (targetAddress == null)
            {
                HttpContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }

            var proxiedRequest = HttpProxy.CreateProxiedHttpRequest(HttpContext, (uri) => ReplaceUriAddress(uri, targetAddress));

            using var client = httpClientFactory.CreateClient(Constants.HttpClientNames.AdapterProxyClient);
            var response = await client.SendAsync(proxiedRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            await StreamProxiedSseHttpResponseAsync(HttpContext, name, response, targetAddress, cancellationToken).ConfigureAwait(false);
        }

        private async Task StreamProxiedSseHttpResponseAsync(HttpContext context, string name, HttpResponseMessage response, string targetAddress, CancellationToken cancellationToken)
        {
            context.Response.StatusCode = (int)response.StatusCode;

            foreach (var header in response.Headers)
                context.Response.Headers[header.Key] = header.Value.ToArray();
            foreach (var header in response.Content.Headers)
                context.Response.Headers[header.Key] = header.Value.ToArray();

            context.Response.Headers.Remove("transfer-encoding");
            context.Features.Get<AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            var buffer = new byte[8192];
            string? sessionId = null;
            while (!cancellationToken.IsCancellationRequested)
            {
                var chunkLength = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (chunkLength > 0)
                {
                    // There's an issue that the server response from the initialization call does not respect the base url.
                    // Before awaiting fix from MCP, having gateway to rewrite the response from the MCP server during the initialization phrase to append the prefix routing.
                    if (sessionId == null)
                    {
                        var chunkValue = Encoding.UTF8.GetString(buffer, 0, chunkLength);
                        var index = chunkValue.IndexOf(MCPSSEMarker, StringComparison.OrdinalIgnoreCase);
                        if (index >= 0)
                        {
                            var modified = chunkValue.Replace(MCPSSEMarker, $"/adapters/{name}{MCPSSEMarker}");
                            sessionId = chunkValue[(index + MCPSSEMarker.Length)..].Trim();

                            if (!string.IsNullOrEmpty(sessionId))
                            {
                                await sessionStore.SetAsync(sessionId, targetAddress, cancellationToken).ConfigureAwait(false);
                            }

                            await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(modified), cancellationToken).ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        await context.Response.Body.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
                    }

                    await context.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private static Uri ReplaceUriAddress(Uri originalUri, string newAddress)
        {
            ArgumentNullException.ThrowIfNull(originalUri, nameof(originalUri));
            ArgumentException.ThrowIfNullOrEmpty(newAddress, nameof(newAddress));

            var segments = originalUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

            var newBaseUri = new Uri(newAddress, UriKind.Absolute);
            var path = '/' + string.Join('/', segments.Skip(2));
            if (path.EndsWith("/messages") || path.EndsWith("/mcp"))
                path += "/";

            var newUriBuilder = new UriBuilder(newBaseUri.Scheme, newBaseUri.Host, newBaseUri.Port)
            {
                Path = path,
                Query = originalUri.Query.TrimStart('?'),
                Fragment = originalUri.Fragment.TrimStart('#')
            };

            return newUriBuilder.Uri;
        }
    }
}
