// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.McpGateway.Management.Authorization;
using Microsoft.McpGateway.Management.Extensions;
using Microsoft.McpGateway.Management.Store;
using Microsoft.McpGateway.Service.Routing;

namespace Microsoft.McpGateway.Service.Controllers
{
    [ApiController]
    [Authorize]
    public class AdapterReverseProxyController(
        IHttpClientFactory httpClientFactory,
        IServiceNodeInfoProvider serviceNodeInfoProvider,
        IAdapterResourceStore adapterResourceStore,
        IPermissionProvider permissionProvider,
        ILogger<AdapterReverseProxyController> logger) : ControllerBase
    {
        private const string ToolGateway = "toolgateway";
        private readonly IHttpClientFactory httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        private readonly IServiceNodeInfoProvider serviceNodeInfoProvider = serviceNodeInfoProvider ?? throw new ArgumentNullException(nameof(serviceNodeInfoProvider));
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

            var adapterName = name ?? ToolGateway;
            var nodes = await serviceNodeInfoProvider.GetNodeAddressesAsync(adapterName, cancellationToken).ConfigureAwait(false);
            if (nodes.Count == 0)
            {
                HttpContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }

            var targetAddress = nodes.Values.ElementAt(Random.Shared.Next(nodes.Count));
            // Only the first-party tool gateway route (no adapter name on the URL) is trusted with the
            // shared gateway secret. Adapter pods are user supplied and must never receive it.
            using var proxiedRequest = HttpProxy.CreateProxiedHttpRequest(HttpContext, (uri) => ReplaceUriAddress(uri, targetAddress), forwardGatewaySecret: name is null);

            using var client = httpClientFactory.CreateClient(Constants.HttpClientNames.AdapterProxyClient);
            using var response = await client.SendAsync(proxiedRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

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

            var newUriBuilder = new UriBuilder(newBaseUri.Scheme, newBaseUri.Host, newBaseUri.Port)
            {
                Path = path,
                Query = QueryString.Create(QueryHelpers.ParseQuery(originalUri.Query)
                    .Where(parameter => !string.Equals(parameter.Key, "session_id", StringComparison.OrdinalIgnoreCase))).Value,
                Fragment = originalUri.Fragment.TrimStart('#')
            };

            return newUriBuilder.Uri;
        }
    }
}
