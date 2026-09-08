using Microsoft.McpGateway.Management.Contracts;

namespace Microsoft.McpGateway.Service;

public sealed class McpEndpointMiddleware(RequestDelegate next, IConfiguration configuration)
{
    private readonly HashSet<string> allowedOrigins = new(
        (configuration.GetSection("Mcp:AllowedOrigins").Get<string[]>() ?? [])
            .Append(configuration["PublicOrigin"] ?? "http://localhost:8000")
            .Select(origin => new Uri(origin, UriKind.Absolute).GetLeftPart(UriPartial.Authority)),
        StringComparer.OrdinalIgnoreCase);

    public async Task InvokeAsync(HttpContext context)
    {
        var segments = context.Request.Path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries) ?? [];
        var isMcp = segments.Length == 1 && segments[0].Equals("mcp", StringComparison.OrdinalIgnoreCase) ||
            segments.Length == 3 && segments[0].Equals("adapters", StringComparison.OrdinalIgnoreCase) &&
            segments[2].Equals("mcp", StringComparison.OrdinalIgnoreCase);
        if (!isMcp)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        if (context.Request.Headers.TryGetValue("Origin", out var origins) &&
            (origins.Count != 1 || !allowedOrigins.Contains(origins.ToString())))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

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
                new { supported = new[] { McpProtocol.Version }, requested = version.ToString() }).ConfigureAwait(false);
            return;
        }

        var method = context.Request.Headers["Mcp-Method"];
        if (method.Count != 1 || string.IsNullOrWhiteSpace(method[0]))
        {
            await RejectAsync(context, -32020, "A single Mcp-Method header is required.").ConfigureAwait(false);
            return;
        }

        if (method[0] is "tools/call" or "resources/read" or "prompts/get")
        {
            var name = context.Request.Headers["Mcp-Name"];
            if (name.Count != 1 || string.IsNullOrWhiteSpace(name[0]))
            {
                await RejectAsync(context, -32020, "A single Mcp-Name header is required for this method.").ConfigureAwait(false);
                return;
            }
        }

        await next(context).ConfigureAwait(false);
    }

    private static Task RejectAsync(HttpContext context, int code, string message, object? data = null)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return context.Response.WriteAsJsonAsync(new { jsonrpc = "2.0", error = new { code, message, data } }, context.RequestAborted);
    }
}