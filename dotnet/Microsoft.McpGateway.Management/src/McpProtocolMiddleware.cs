using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.McpGateway.Management.Contracts;
using Microsoft.Net.Http.Headers;
using ModelContextProtocol;

namespace Microsoft.McpGateway.Management;

public sealed class McpProtocolMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!HttpMethods.IsPost(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            context.Response.Headers.Allow = "POST";
            return;
        }

        var version = context.Request.Headers[McpProtocol.VersionHeader];
        if (version.Count != 1 || string.IsNullOrWhiteSpace(version[0]))
        {
            await RejectAsync(context, -32020, "A single MCP-Protocol-Version header is required.").ConfigureAwait(false);
            return;
        }

        if (version[0] != McpProtocol.Version)
        {
            await RejectAsync(context, -32022, "This gateway requires MCP 2026-07-28. Upgrade the client and adapter, or use the legacy gateway image.",
                data: new { supported = new[] { McpProtocol.Version }, requested = version.ToString() }).ConfigureAwait(false);
            return;
        }

        var method = context.Request.Headers["Mcp-Method"];
        if (method.Count != 1 || string.IsNullOrWhiteSpace(method[0]))
        {
            await RejectAsync(context, -32020, "A single Mcp-Method header is required.").ConfigureAwait(false);
            return;
        }

        if (method[0] is "initialize" or "notifications/initialized")
        {
            await RejectAsync(context, -32601, "Legacy initialization is not supported by MCP 2026-07-28.").ConfigureAwait(false);
            return;
        }

        if (!MediaTypeHeaderValue.TryParseStrictList(context.Request.Headers.Accept, out var acceptedTypes) ||
            !acceptedTypes.Any(value => value.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) && value.Quality.GetValueOrDefault(1) > 0) ||
            !acceptedTypes.Any(value => value.MediaType.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase) && value.Quality.GetValueOrDefault(1) > 0))
        {
            await RejectAsync(context, -32020, "Accept must include application/json and text/event-stream.",
                statusCode: StatusCodes.Status406NotAcceptable).ConfigureAwait(false);
            return;
        }

        var requiresName = method[0] is "tools/call" or "resources/read" or "prompts/get";
        var name = context.Request.Headers["Mcp-Name"];
        if (requiresName && (name.Count != 1 || string.IsNullOrWhiteSpace(name[0])))
        {
            await RejectAsync(context, -32020, "A single Mcp-Name header is required for this method.").ConfigureAwait(false);
            return;
        }

        if (!MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var contentType) ||
            !contentType.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase))
        {
            await RejectAsync(context, -32600, "Content-Type must be application/json.",
                statusCode: StatusCodes.Status415UnsupportedMediaType).ConfigureAwait(false);
            return;
        }

        context.Request.EnableBuffering();
        var bodyPosition = context.Request.Body.Position;
        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            await RejectAsync(context, -32700, "The request body must contain valid JSON.").ConfigureAwait(false);
            return;
        }
        finally
        {
            context.Request.Body.Position = bodyPosition;
        }

        using (document)
        {
            var message = document.RootElement;
            if (message.ValueKind != JsonValueKind.Object ||
                !message.TryGetProperty("jsonrpc", out var jsonrpc) || jsonrpc.ValueKind != JsonValueKind.String || jsonrpc.GetString() != "2.0" ||
                !message.TryGetProperty("method", out var bodyMethod) || bodyMethod.ValueKind != JsonValueKind.String)
            {
                await RejectAsync(context, -32600, "A single JSON-RPC 2.0 request is required.").ConfigureAwait(false);
                return;
            }

            JsonElement? id = message.TryGetProperty("id", out var requestId) && requestId.ValueKind is JsonValueKind.String or JsonValueKind.Number
                ? requestId : null;
            if (bodyMethod.GetString() != method[0])
            {
                await RejectAsync(context, -32020, "Mcp-Method must match the JSON-RPC method.", id).ConfigureAwait(false);
                return;
            }

            if (!message.TryGetProperty("params", out var parameters) || parameters.ValueKind != JsonValueKind.Object ||
                !parameters.TryGetProperty("_meta", out var metadata) || metadata.ValueKind != JsonValueKind.Object ||
                !metadata.TryGetProperty("io.modelcontextprotocol/protocolVersion", out var bodyVersion) || bodyVersion.ValueKind != JsonValueKind.String ||
                !metadata.TryGetProperty("io.modelcontextprotocol/clientCapabilities", out var capabilities) || capabilities.ValueKind != JsonValueKind.Object)
            {
                await RejectAsync(context, -32020, "params._meta must include a protocol version and a client capabilities object.", id).ConfigureAwait(false);
                return;
            }

            if (bodyVersion.GetString() != version[0])
            {
                await RejectAsync(context, -32020, "MCP-Protocol-Version must match the protocol version in params._meta.", id).ConfigureAwait(false);
                return;
            }

            if (requiresName)
            {
                var nameProperty = method[0] == "resources/read" ? "uri" : "name";
                try
                {
                    if (!parameters.TryGetProperty(nameProperty, out var bodyName) || bodyName.ValueKind != JsonValueKind.String ||
                        McpProtocol.DecodeHeader(name[0]!) != bodyName.GetString())
                    {
                        await RejectAsync(context, -32020, $"Mcp-Name must match params.{nameProperty}.", id).ConfigureAwait(false);
                        return;
                    }
                }
                catch (McpProtocolException exception)
                {
                    await RejectAsync(context, -32020, exception.Message, id).ConfigureAwait(false);
                    return;
                }
            }
        }

        await next(context).ConfigureAwait(false);
    }

    private static Task RejectAsync(HttpContext context, int code, string message, JsonElement? id = null,
        object? data = null, int statusCode = StatusCodes.Status400BadRequest)
    {
        context.Response.StatusCode = statusCode;
        return context.Response.WriteAsJsonAsync(new { jsonrpc = "2.0", id, error = new { code, message, data } }, context.RequestAborted);
    }
}