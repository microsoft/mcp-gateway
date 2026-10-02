namespace Microsoft.McpGateway.Service;

public sealed class McpEndpointMiddleware(RequestDelegate next, IConfiguration configuration)
{
    private readonly HashSet<string> allowedOrigins = new(
        (configuration.GetSection("Mcp:AllowedOrigins").Get<string[]>() ?? [])
            .Append(configuration["PublicOrigin"] ?? "http://localhost:8000")
            .Select(origin => new Uri(origin, UriKind.Absolute).GetLeftPart(UriPartial.Authority)),
        StringComparer.OrdinalIgnoreCase);

    public static bool IsMcpEndpoint(HttpContext context)
    {
        var segments = context.Request.Path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries) ?? [];
        return segments.Length == 1 && segments[0].Equals("mcp", StringComparison.OrdinalIgnoreCase) ||
            segments.Length == 3 && segments[0].Equals("adapters", StringComparison.OrdinalIgnoreCase) &&
            segments[2].Equals("mcp", StringComparison.OrdinalIgnoreCase);
    }

    public Task InvokeAsync(HttpContext context)
    {
        if (IsMcpEndpoint(context) && context.Request.Headers.TryGetValue("Origin", out var origins) &&
            (origins.Count != 1 || !allowedOrigins.Contains(origins.ToString())))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        }

        return next(context);
    }
}