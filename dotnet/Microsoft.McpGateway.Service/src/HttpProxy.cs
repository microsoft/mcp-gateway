// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Net.Http.Headers;
using Microsoft.McpGateway.Management.Authorization;
using Microsoft.McpGateway.Management.Extensions;

namespace Microsoft.McpGateway.Service
{
    public static class HttpProxy
    {
        /// <summary>
        /// Builds the outbound request used to proxy <paramref name="context"/> to a backend pod.
        /// </summary>
        /// <param name="forwardGatewaySecret">
        /// Whether to attach the shared <c>X-Gateway-Secret</c> credential. The tool gateway requires it
        /// before it will trust the forwarded identity headers, so it may only be sent to first-party
        /// internal targets; user-deployed adapter pods must never receive it. Defaults to
        /// <see langword="false"/> so new call sites fail closed.
        /// </param>
        public static HttpRequestMessage CreateProxiedHttpRequest(HttpContext context, Func<Uri, Uri>? targetOverride = null, bool forwardGatewaySecret = false)
        {
            var hasBody = context.Request.ContentLength > 0 ||
                          context.Request.ContentLength is null && !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method) && !HttpMethods.IsDelete(context.Request.Method) && !HttpMethods.IsOptions(context.Request.Method);

            if (hasBody && !context.Request.Body.CanSeek)
            {
                context.Request.EnableBuffering();
            }
            var requestMessage = new HttpRequestMessage
            {
                Method = new HttpMethod(context.Request.Method),
                RequestUri = targetOverride == null ? new Uri(context.Request.GetEncodedUrl()) : targetOverride(new Uri(context.Request.GetEncodedUrl())),
                Content = hasBody ? new StreamContent(context.Request.Body) : null
            };

            foreach (var header in context.Request.Headers)
            {
                if (string.Equals(header.Key, "Mcp-Session-Id", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(header.Key, "Last-Event-ID", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(header.Key, HeaderNames.Host, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(header.Key, HeaderNames.Connection, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(header.Key, HeaderNames.TransferEncoding, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(header.Key, "Forwarded", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Skip the inbound Authorization header
                if (string.Equals(header.Key, HeaderNames.Authorization, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Skip identity headers entirely - they will be re-injected from the authenticated principal below
                if (string.Equals(header.Key, ForwardedIdentityHeaders.UserId, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(header.Key, ForwardedIdentityHeaders.UserName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(header.Key, ForwardedIdentityHeaders.Roles, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(header.Key, ForwardedIdentityHeaders.GatewaySecret, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!requestMessage.Headers.TryAddWithoutValidation(header.Key, [.. header.Value]))
                    requestMessage.Content?.Headers.TryAddWithoutValidation(header.Key, [.. header.Value]);
            }

            if (requestMessage.Content is not null)
            {
                requestMessage.Content.Headers.ContentLength = context.Request.ContentLength;
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

            if (forwardGatewaySecret)
            {
                var gatewaySecret = context.RequestServices.GetService<IConfiguration>()?.GetValue<string>("GatewaySettings:Secret");
                if (!string.IsNullOrEmpty(gatewaySecret))
                    requestMessage.Headers.TryAddWithoutValidation(ForwardedIdentityHeaders.GatewaySecret, gatewaySecret);
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
            "X-Accel-Buffering",
            "Allow",
        };

        public static async Task CopyProxiedHttpResponseAsync(HttpContext context, HttpResponseMessage response, CancellationToken cancellationToken)
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

            if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                await response.Content.CopyToAsync(context.Response.Body, cancellationToken).ConfigureAwait(false);
                return;
            }

            context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            context.Response.Headers["X-Accel-Buffering"] = "no";
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var buffer = new byte[8192];
            int bytesRead;
            while ((bytesRead = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
            {
                await context.Response.Body.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
                await context.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }
}