// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.RegularExpressions;
using Microsoft.McpGateway.Management.Extensions;
using Microsoft.McpGateway.Service.Routing;

namespace Microsoft.McpGateway.Service.Session
{
    public class AdapterSessionRoutingHandler(IServiceNodeInfoProvider serviceNodeInfoProvider, IAdapterSessionStore sessionStore, ILogger<AdapterSessionRoutingHandler> logger) : ISessionRoutingHandler
    {
        // Session IDs are issued by downstream adapter pods (untrusted). Constrain to a
        // conservative character set/length to keep them safe to use as cache keys and to log,
        // and to prevent a malicious adapter from injecting separators that would let it forge
        // a different user's scoped session key.
        private static readonly Regex SessionIdPattern = new(@"^[a-zA-Z0-9\-]{1,128}$", RegexOptions.Compiled);

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

        public async Task<string?> GetExistingSessionTargetAsync(string adapterName, HttpContext httpContext, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrEmpty(adapterName);

            var sessionId = GetSessionId(httpContext);
            if (string.IsNullOrEmpty(sessionId))
            {
                throw new ArgumentException("Session id not found in the request.");
            }

            // Reject ids that fall outside the allowlist before touching the session store.
            // Only adapter-issued ids that previously passed IsValidSessionId on the way in
            // can possibly match an entry, so a malformed inbound id is a fast-fail.
            if (!IsValidSessionId(sessionId))
            {
                _logger.LogWarning("Rejecting malformed session id for request {path}", httpContext.Request.Path.Sanitize());
                throw new ArgumentException("Session id is not valid, or has expired.");
            }

            // Scope the lookup to both the authenticated user and the adapter named on the
            // request route. Binding to the adapter prevents a session established against one
            // adapter from being replayed through a different adapter's URL after the caller's
            // access to the original adapter has been revoked (stale-session authorization
            // bypass). The route adapter is the same value already authorized by the caller, so
            // a session can only resolve when the route the caller is currently allowed to use
            // matches the route the session was created on.
            var scopedKey = BuildScopedSessionKey(httpContext, adapterName, sessionId);

            var (targetAddress, exists) = await _sessionStore.TryGetAsync(scopedKey, cancellationToken).ConfigureAwait(false);
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

        /// <summary>
        /// Validates that a session id supplied by a downstream adapter has a safe shape before
        /// it is persisted in the session store. Rejects values that contain separators or
        /// otherwise look like an attempt to break out of the scoped key namespace.
        /// </summary>
        public static bool IsValidSessionId(string? sessionId) =>
            !string.IsNullOrEmpty(sessionId) && SessionIdPattern.IsMatch(sessionId);

        /// <summary>
        /// Computes the session store key used for a given (user, adapter, session id) tuple.
        /// Binding the session id to the authenticated user's id prevents cross-tenant session
        /// hijacking even when an attacker can observe or guess another user's raw session id.
        /// Binding it to the adapter name additionally prevents a session created on one adapter
        /// route from being replayed through another adapter route, which would otherwise allow a
        /// caller whose access to the original adapter was revoked to keep reaching its backend.
        /// Both <paramref name="adapterName"/> (<c>^[a-z0-9-]+$</c>) and <paramref name="sessionId"/>
        /// (<c>^[a-zA-Z0-9-]{1,128}$</c>) are colon-free, so the composed key is unambiguous.
        /// </summary>
        /// <exception cref="UnauthorizedAccessException">
        /// Thrown when the request has no authenticated user id. Endpoints that use the session
        /// store are expected to be guarded by <c>[Authorize]</c>; missing identity here means
        /// the request must be rejected rather than silently routed.
        /// </exception>
        public static string BuildScopedSessionKey(HttpContext httpContext, string adapterName, string sessionId)
        {
            ArgumentNullException.ThrowIfNull(httpContext);
            ArgumentException.ThrowIfNullOrEmpty(adapterName);
            ArgumentException.ThrowIfNullOrEmpty(sessionId);

            var userId = httpContext.User?.GetUserId();
            if (string.IsNullOrWhiteSpace(userId))
            {
                throw new UnauthorizedAccessException("Authenticated user is required to access a session.");
            }

            return $"{userId}:{adapterName}:{sessionId}";
        }
    }
}
