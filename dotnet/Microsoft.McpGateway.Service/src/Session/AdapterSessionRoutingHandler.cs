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

        public async Task<string?> GetExistingSessionTargetAsync(HttpContext httpContext, CancellationToken cancellationToken)
        {
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

            // Scope the lookup to the authenticated user so a client cannot route into another
            // user's session by supplying that user's session id.
            var scopedKey = BuildScopedSessionKey(httpContext, sessionId);

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
        /// Computes the session store key used for a given (user, session id) pair. Binding the
        /// session id to the authenticated user's id prevents cross-tenant session hijacking
        /// even when an attacker can observe or guess another user's raw session id.
        /// </summary>
        /// <exception cref="UnauthorizedAccessException">
        /// Thrown when the request has no authenticated user id. Endpoints that use the session
        /// store are expected to be guarded by <c>[Authorize]</c>; missing identity here means
        /// the request must be rejected rather than silently routed.
        /// </exception>
        public static string BuildScopedSessionKey(HttpContext httpContext, string sessionId)
        {
            ArgumentNullException.ThrowIfNull(httpContext);
            ArgumentException.ThrowIfNullOrEmpty(sessionId);

            var userId = httpContext.User?.GetUserId();
            if (string.IsNullOrWhiteSpace(userId))
            {
                throw new UnauthorizedAccessException("Authenticated user is required to access a session.");
            }

            return $"{userId}:{sessionId}";
        }
    }
}
