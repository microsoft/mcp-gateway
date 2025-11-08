// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.McpGateway.Service.Session;

namespace Microsoft.McpGateway.Service.Controllers
{
    [ApiController]
    [Authorize]
    public class AdapterReverseProxyController(IHttpClientFactory httpClientFactory, IAdapterSessionStore sessionStore, ISessionRoutingHandler sessionRoutingHandler) : ControllerBase
    {
        private const string ToolGateway = "toolgateway";
        private readonly IHttpClientFactory httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        private readonly IAdapterSessionStore sessionStore = sessionStore ?? throw new ArgumentNullException(nameof(sessionStore));
        private readonly ISessionRoutingHandler sessionRoutingHandler = sessionRoutingHandler ?? throw new ArgumentNullException(nameof(sessionRoutingHandler));

        /// <summary>
        /// Support for MCP streamable HTTP connection.
        /// </summary>
        [HttpPost("mcp")]
        [HttpPost("adapters/{name}/mcp")]
        public async Task ForwardStreamableHttpRequest(string? name, CancellationToken cancellationToken)
        {
            var sessionId = AdapterSessionRoutingHandler.GetSessionId(HttpContext);
            string? targetAddress;
            if (string.IsNullOrEmpty(sessionId))
                targetAddress = await sessionRoutingHandler.GetNewSessionTargetAsync(name ?? ToolGateway, HttpContext, cancellationToken).ConfigureAwait(false);
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

        private static Uri ReplaceUriAddress(Uri originalUri, string newAddress)
        {
            ArgumentNullException.ThrowIfNull(originalUri);
            ArgumentException.ThrowIfNullOrEmpty(newAddress);

            var segments = originalUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

            var newBaseUri = new Uri(newAddress, UriKind.Absolute);
            var path = '/' + string.Join('/', segments.Skip(2));
            if (path.EndsWith("/messages"))
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
