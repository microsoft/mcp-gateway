// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.McpGateway.Management.Authorization;
using Microsoft.McpGateway.Management.Extensions;
using Microsoft.McpGateway.Management.Store;
using Microsoft.McpGateway.Service.Session;

namespace Microsoft.McpGateway.Service.Controllers
{
    [ApiController]
    [Authorize]
    public class AdapterReverseProxyController(
        IHttpClientFactory httpClientFactory,
        IAdapterSessionStore sessionStore,
        ISessionRoutingHandler sessionRoutingHandler,
        IAdapterResourceStore adapterResourceStore,
        IPermissionProvider permissionProvider,
        ILogger<AdapterReverseProxyController> logger) : ControllerBase
    {
        private const string ToolGateway = "toolgateway";
        private readonly IHttpClientFactory httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        private readonly IAdapterSessionStore sessionStore = sessionStore ?? throw new ArgumentNullException(nameof(sessionStore));
        private readonly ISessionRoutingHandler sessionRoutingHandler = sessionRoutingHandler ?? throw new ArgumentNullException(nameof(sessionRoutingHandler));
        private readonly IAdapterResourceStore adapterResourceStore = adapterResourceStore ?? throw new ArgumentNullException(nameof(adapterResourceStore));
        private readonly IPermissionProvider permissionProvider = permissionProvider ?? throw new ArgumentNullException(nameof(permissionProvider));
        private readonly ILogger<AdapterReverseProxyController> logger = logger ?? throw new ArgumentNullException(nameof(logger));

        /// <summary>
        /// Support for MCP streamable HTTP connection.
        /// </summary>
        [HttpPost("mcp")]
        [HttpPost("adapters/{name}/mcp")]
        public async Task ForwardStreamableHttpRequest(string? name, CancellationToken cancellationToken)
        {
            if (!await EnsureAdapterReadAccessAsync(name, cancellationToken).ConfigureAwait(false))
                return;

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
                {
                    // The session id is supplied by the downstream adapter, which is not fully
                    // trusted. Validate its shape before persisting; a malicious adapter could
                    // otherwise return a crafted value that pollutes another user's scoped key
                    // or breaks routing semantics.
                    if (!AdapterSessionRoutingHandler.IsValidSessionId(sessionId))
                    {
                        logger.LogWarning("Downstream adapter returned an invalid session id for adapter {adapterName}.", (name ?? ToolGateway).Sanitize());

                        // Drop the invalid value from the proxied response so clients cannot
                        // cache or replay it — there is no scoped-key entry it could ever
                        // resolve to, and forwarding it would create a confusing handle.
                        response.Headers.Remove("mcp-session-id");
                    }
                    else
                    {
                        // Bind the session to the authenticated user so subsequent lookups
                        // require both the session id and the same user identity.
                        var scopedKey = AdapterSessionRoutingHandler.BuildScopedSessionKey(HttpContext, sessionId);
                        await sessionStore.SetAsync(scopedKey, targetAddress, cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            await HttpProxy.CopyProxiedHttpResponseAsync(HttpContext, response, cancellationToken).ConfigureAwait(false);
        }

        private async Task<bool> EnsureAdapterReadAccessAsync(string? name, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return true;
            }

            var adapter = await adapterResourceStore.TryGetAsync(name, cancellationToken).ConfigureAwait(false);
            if (adapter == null)
            {
                logger.LogWarning("Adapter {adapterName} not found while attempting proxy access.", name.Sanitize());
                HttpContext.Response.StatusCode = StatusCodes.Status404NotFound;
                return false;
            }

            if (!await permissionProvider.CheckAccessAsync(HttpContext.User, adapter, Operation.Read).ConfigureAwait(false))
            {
                logger.LogWarning("User {userId} denied read access for adapter {adapterName} via proxy.", HttpContext.User?.Identity?.Name?.Sanitize(), name.Sanitize());
                HttpContext.Response.StatusCode = StatusCodes.Status403Forbidden;
                return false;
            }

            return true;
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
