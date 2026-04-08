// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Net.Http.Headers;
using Microsoft.McpGateway.Management.Authorization;
using Microsoft.McpGateway.Management.Extensions;

namespace Microsoft.McpGateway.Service
{
    public static class HttpProxy
    {
        public static HttpRequestMessage CreateProxiedHttpRequest(HttpContext context, Func<Uri, Uri>? targetOverride = null)
        {
            var hasBody = context.Request.ContentLength > 0 ||
                          context.Request.ContentLength is null && !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method) && !HttpMethods.IsDelete(context.Request.Method) && !HttpMethods.IsOptions(context.Request.Method);

            var requestMessage = new HttpRequestMessage
            {
                Method = new HttpMethod(context.Request.Method),
                RequestUri = targetOverride == null ? new Uri(context.Request.GetEncodedUrl()) : targetOverride(new Uri(context.Request.GetEncodedUrl())),
                Content = hasBody ? new StreamContent(context.Request.Body) : null
            };

            foreach (var header in context.Request.Headers)
            {
                // Skip the inbound Authorization header
                if (string.Equals(header.Key, HeaderNames.Authorization, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Skip identity headers entirely - they will be re-injected from the authenticated principal below
                if (string.Equals(header.Key, ForwardedIdentityHeaders.UserId, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(header.Key, ForwardedIdentityHeaders.UserName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(header.Key, ForwardedIdentityHeaders.Roles, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!requestMessage.Headers.TryAddWithoutValidation(header.Key, [.. header.Value]))
                    requestMessage.Content?.Headers.TryAddWithoutValidation(header.Key, [.. header.Value]);
            }

            var principal = context.User;
            if (principal?.Identity?.IsAuthenticated == true)
            {
                var userId = principal.GetUserId() ?? principal.Identity?.Name;

                if (!string.IsNullOrWhiteSpace(userId))
                    requestMessage.Headers.TryAddWithoutValidation(ForwardedIdentityHeaders.UserId, userId);

                var roles = principal.GetUserRoles();

                if (roles.Count > 0)
                    requestMessage.Headers.TryAddWithoutValidation(ForwardedIdentityHeaders.Roles, string.Join(',', roles));
            }

            requestMessage.Headers.TryAddWithoutValidation("Forwarded", $"for={context.Connection.RemoteIpAddress};proto={context.Request.Scheme};host={context.Request.Host.Value}");
            return requestMessage;
        }

        // Response headers that are safe to forward from backend pods to clients.
        private static readonly HashSet<string> AllowedResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
        {
            "Content-Type",
            "Content-Length",
            "Content-Encoding",
            "Content-Language",
            "Content-Range",
            "Cache-Control",
            "ETag",
            "Last-Modified",
            "Accept-Ranges",
            "Vary",
            "Date",
            "Retry-After",
            "X-Request-Id",
            "X-Correlation-Id",
            "mcp-session-id",
        };

        public static Task CopyProxiedHttpResponseAsync(HttpContext context, HttpResponseMessage response, CancellationToken cancellationToken)
        {
            context.Response.StatusCode = (int)response.StatusCode;

            foreach (var header in response.Headers)
            {
                if (AllowedResponseHeaders.Contains(header.Key))
                    context.Response.Headers[header.Key] = header.Value.ToArray();
            }
            foreach (var header in response.Content.Headers)
            {
                if (AllowedResponseHeaders.Contains(header.Key))
                    context.Response.Headers[header.Key] = header.Value.ToArray();
            }

            context.Response.Headers.Remove(HeaderNames.TransferEncoding);

            return response.Content.CopyToAsync(context.Response.Body, cancellationToken);
        }
    }
}
