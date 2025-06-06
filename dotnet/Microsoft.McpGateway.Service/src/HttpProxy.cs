// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.AspNetCore.Http.Extensions;

namespace Microsoft.McpGateway.Service
{
    public static class HttpProxy
    {
        public static HttpRequestMessage CreateProxiedHttpRequest(HttpContext context, Func<Uri, Uri>? targetOverride = null)
        {
            var requestMessage = new HttpRequestMessage
            {
                Method = new HttpMethod(context.Request.Method),
                RequestUri = targetOverride == null ? new Uri(context.Request.GetEncodedUrl()) : targetOverride(new Uri(context.Request.GetEncodedUrl())),
                Content = context.Request.ContentLength > 0 ? new StreamContent(context.Request.Body) : null
            };

            foreach (var header in context.Request.Headers)
            {
                if (!requestMessage.Headers.TryAddWithoutValidation(header.Key, [.. header.Value]))
                    requestMessage.Content?.Headers.TryAddWithoutValidation(header.Key, [.. header.Value]);
            }

            requestMessage.Headers.TryAddWithoutValidation("Forwarded", $"for={context.Connection.RemoteIpAddress};proto={context.Request.Scheme};host={context.Request.Host.Value}");
            return requestMessage;
        }

        public static Task CopyProxiedHttpResponseAsync(HttpContext context, HttpResponseMessage response, CancellationToken cancellationToken)
        {
            context.Response.StatusCode = (int)response.StatusCode;

            foreach (var header in response.Headers)
                context.Response.Headers[header.Key] = header.Value.ToArray();
            foreach (var header in response.Content.Headers)
                context.Response.Headers[header.Key] = header.Value.ToArray();

            context.Response.Headers.Remove("transfer-encoding");

            return response.Content.CopyToAsync(context.Response.Body, cancellationToken);
        }
    }
}
