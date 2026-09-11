using Microsoft.McpGateway.Management;

namespace Microsoft.McpGateway.Service;

public sealed class McpEndpointMiddleware(RequestDelegate next, IConfiguration configuration)
{
    private readonly McpProtocolMiddleware protocolMiddleware = new(next);
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

        await protocolMiddleware.InvokeAsync(context).ConfigureAwait(false);
    }
}