// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.McpGateway.Management.Extensions;
using Microsoft.McpGateway.Service.Routing;

namespace Microsoft.McpGateway.Service.Session
{
    public class AdapterSessionRoutingHandler(IServiceNodeInfoProvider serviceNodeInfoProvider, IAdapterSessionStore sessionStore, ILogger<AdapterSessionRoutingHandler> logger) : ISessionRoutingHandler
    {
        private readonly IServiceNodeInfoProvider _serviceNodeInfoProvider = serviceNodeInfoProvider ?? throw new ArgumentNullException(nameof(serviceNodeInfoProvider));
        private readonly IAdapterSessionStore _sessionStore = sessionStore ?? throw new ArgumentNullException(nameof(sessionStore));
        private readonly ILogger<AdapterSessionRoutingHandler> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        public async Task<string?> GetNewSessionTargetAsync(string adapterName, HttpContext httpContext, CancellationToken cancellationToken)
        {
            var allPods = await _serviceNodeInfoProvider.GetNodeAddressesAsync(adapterName, cancellationToken).ConfigureAwait(false);
            if (allPods.Count == 0)
                return null;
            var selected = Random.Shared.Next(allPods.Count);

            var targetAddress = allPods.ElementAt(selected).Value;
            return targetAddress;
        }

        public async Task<string?> GetExistingSessionTargetAsync(HttpContext httpContext, CancellationToken cancellationToken)
        {
            var sessionId = GetSessionId(httpContext);
            if (string.IsNullOrEmpty(sessionId))
            {
                throw new ArgumentException("Session id not found in the request.");
            }

            var (targetAddress, exists) = await _sessionStore.TryGetAsync(sessionId, cancellationToken).ConfigureAwait(false);
            if (!exists || targetAddress == null)
            {
                _logger.LogWarning("Cannot find session id for request {path}", httpContext.Request.Path.Sanitize());
                throw new ArgumentException("Session id is not valid, or has expired.");
            }
            _logger.LogInformation("Existing session id {sessionId} found for request {path}", sessionId.Sanitize(), httpContext.Request.Path.Sanitize());
            return targetAddress!;
        }

        public static string? GetSessionId(HttpContext httpContext)
        {
            var sessionId = httpContext.Request.Query["session_id"];
            if (string.IsNullOrEmpty(sessionId))
                sessionId = httpContext.Request.Headers["mcp-session-id"];
            return sessionId;
        }

        public static string? GetSessionId(HttpResponseMessage responseMessage)
        {
            responseMessage.EnsureSuccessStatusCode();
            if (responseMessage.Headers.TryGetValues("mcp-session-id", out var sessionIdValues))
            {
                var sessionId = sessionIdValues.FirstOrDefault();
                return sessionId;
            }
            return string.Empty;
        }
    }
}
