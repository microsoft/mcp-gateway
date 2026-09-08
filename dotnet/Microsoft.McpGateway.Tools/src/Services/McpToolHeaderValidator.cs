using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Microsoft.McpGateway.Tools.Services;

public static class McpToolHeaderValidator
{
    private static readonly Regex HeaderToken = new("^[!#$%&'*+.^_`|~a-zA-Z0-9-]+$", RegexOptions.CultureInvariant);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private const decimal MaxSafeInteger = 9007199254740991;

    public static void Validate(JsonElement schema, IDictionary<string, JsonElement>? arguments, IHeaderDictionary headers)
    {
        var values = JsonSerializer.SerializeToElement(arguments ?? new Dictionary<string, JsonElement>());
        ValidateProperties(schema, values, headers, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    private static void ValidateProperties(JsonElement schema, JsonElement values, IHeaderDictionary headers, HashSet<string> names)
    {
        if (schema.ValueKind != JsonValueKind.Object ||
            !schema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in properties.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object)
                continue;
            JsonElement value = default;
            if (values.ValueKind == JsonValueKind.Object)
                values.TryGetProperty(property.Name, out value);
            ValidateProperties(property.Value, value, headers, names);

            if (!property.Value.TryGetProperty("x-mcp-header", out var annotation))
                continue;
            var name = annotation.ValueKind == JsonValueKind.String ? annotation.GetString() : null;
            if (string.IsNullOrEmpty(name) || !HeaderToken.IsMatch(name) || !names.Add(name))
                Reject("Invalid or duplicate x-mcp-header annotation.");

            var headerName = $"Mcp-Param-{name}";
            var supplied = headers[headerName];
            if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                if (supplied.Count > 0)
                    Reject($"{headerName} must be omitted when its argument is absent or null.");
                continue;
            }

            if (supplied.Count != 1 || supplied[0] is not { } raw)
            {
                Reject($"A single {headerName} header is required.");
                return;
            }

            var decoded = Decode(raw);
            var matches = value.ValueKind switch
            {
                JsonValueKind.String => string.Equals(decoded, value.GetString(), StringComparison.Ordinal),
                JsonValueKind.True => decoded == "true",
                JsonValueKind.False => decoded == "false",
                JsonValueKind.Number when IsIntegerSchema(property.Value) =>
                    IsMatchingInteger(decoded, value.GetRawText()),
                _ => false
            };
            if (!matches)
                Reject($"{headerName} does not match its annotated argument.");
        }
    }

    private static string Decode(string value)
    {
        if (value != value.Trim() || value.Any(character => character != '\t' && (character < ' ' || character > '~')))
            Reject("Mirrored headers must use ASCII or the Base64 sentinel encoding.");

        if (!value.StartsWith("=?base64?", StringComparison.Ordinal) || !value.EndsWith("?=", StringComparison.Ordinal))
            return value;

        try
        {
            return StrictUtf8.GetString(Convert.FromBase64String(value[9..^2]));
        }
        catch (Exception exception) when (exception is FormatException or DecoderFallbackException)
        {
            throw new McpProtocolException("Invalid Base64 mirrored header.", McpErrorCode.HeaderMismatch);
        }
    }

    private static bool IsIntegerSchema(JsonElement schema) =>
        schema.TryGetProperty("type", out var type) &&
        (type.ValueKind == JsonValueKind.String && type.GetString() == "integer" ||
            type.ValueKind == JsonValueKind.Array && type.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.String && item.GetString() == "integer"));

    private static bool IsMatchingInteger(string header, string body) =>
        decimal.TryParse(header, NumberStyles.Float, CultureInfo.InvariantCulture, out var headerValue) &&
        decimal.TryParse(body, NumberStyles.Float, CultureInfo.InvariantCulture, out var bodyValue) &&
        headerValue == decimal.Truncate(headerValue) && bodyValue == decimal.Truncate(bodyValue) &&
        Math.Abs(headerValue) <= MaxSafeInteger && Math.Abs(bodyValue) <= MaxSafeInteger && headerValue == bodyValue;

    private static void Reject(string message) => throw new McpProtocolException(message, McpErrorCode.HeaderMismatch);
}