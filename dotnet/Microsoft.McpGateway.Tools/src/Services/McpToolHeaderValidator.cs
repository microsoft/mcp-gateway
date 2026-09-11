using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.McpGateway.Management.Contracts;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Microsoft.McpGateway.Tools.Services;

public static class McpToolHeaderValidator
{
    private static readonly Regex HeaderToken = new(@"\A[!#$%&'*+.^_`|~a-zA-Z0-9-]+\z", RegexOptions.CultureInvariant);
    private const decimal MaxSafeInteger = 9007199254740991;

    public static void Validate(JsonElement schema, IDictionary<string, JsonElement>? arguments, IHeaderDictionary headers)
    {
        var values = JsonSerializer.SerializeToElement(arguments ?? new Dictionary<string, JsonElement>());
        ValidateSchema(schema, values, headers, new HashSet<string>(StringComparer.OrdinalIgnoreCase), true, false);
    }

    private static void ValidateSchema(JsonElement schema, JsonElement values, IHeaderDictionary headers,
        HashSet<string> names, bool reachable, bool isProperty)
    {
        if (schema.ValueKind != JsonValueKind.Object)
            return;

        ValidateHeader(schema, values, headers, names, reachable && isProperty);
        foreach (var child in schema.EnumerateObject())
        {
            if (child.Name == "properties" && child.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in child.Value.EnumerateObject())
                {
                    JsonElement value = default;
                    if (values.ValueKind == JsonValueKind.Object)
                        values.TryGetProperty(property.Name, out value);
                    ValidateSchema(property.Value, value, headers, names, reachable, true);
                }
            }
            else if (child.Name is not ("default" or "examples" or "enum" or "const"))
            {
                if (child.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in child.Value.EnumerateArray())
                        ValidateSchema(item, default, headers, names, false, false);
                }
                else if (child.Value.ValueKind == JsonValueKind.Object)
                {
                    if (child.Name is "$defs" or "definitions" or "patternProperties" or "dependentSchemas")
                    {
                        foreach (var definition in child.Value.EnumerateObject())
                            ValidateSchema(definition.Value, default, headers, names, false, false);
                    }
                    else
                    {
                        ValidateSchema(child.Value, default, headers, names, false, false);
                    }
                }
            }
        }
    }

    private static void ValidateHeader(JsonElement schema, JsonElement value, IHeaderDictionary headers,
        HashSet<string> names, bool reachableProperty)
    {
        if (!schema.TryGetProperty("x-mcp-header", out var annotation))
            return;
        var name = annotation.ValueKind == JsonValueKind.String ? annotation.GetString() : null;
        var type = GetPrimitiveType(schema);
        if (!reachableProperty || string.IsNullOrEmpty(name) || !HeaderToken.IsMatch(name) || !names.Add(name) ||
            type is not ("string" or "integer" or "boolean"))
            Reject("Invalid, unsupported, or duplicate x-mcp-header annotation.");

        var headerName = $"Mcp-Param-{name}";
        var supplied = headers[headerName];
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            if (supplied.Count > 0)
                Reject($"{headerName} must be omitted when its argument is absent or null.");
            return;
        }

        if (supplied.Count != 1 || supplied[0] is not { } raw)
        {
            Reject($"A single {headerName} header is required.");
            return;
        }

        var decoded = McpProtocol.DecodeHeader(raw);
        var matches = (type, value.ValueKind) switch
        {
            ("string", JsonValueKind.String) => string.Equals(decoded, value.GetString(), StringComparison.Ordinal),
            ("boolean", JsonValueKind.True) => decoded == "true",
            ("boolean", JsonValueKind.False) => decoded == "false",
            ("integer", JsonValueKind.Number) => IsMatchingInteger(decoded, value.GetRawText()),
            _ => false
        };
        if (!matches)
            Reject($"{headerName} does not match its annotated argument.");
    }

    private static string? GetPrimitiveType(JsonElement schema)
    {
        if (!schema.TryGetProperty("type", out var type))
            return null;
        var types = type.ValueKind == JsonValueKind.Array ? type.EnumerateArray().ToArray() : [type];
        var primitives = types.Where(item => item.ValueKind != JsonValueKind.String || item.GetString() != "null").ToArray();
        return primitives.Length == 1 && primitives[0].ValueKind == JsonValueKind.String ? primitives[0].GetString() : null;
    }

    private static bool IsMatchingInteger(string header, string body) =>
        decimal.TryParse(header, NumberStyles.Float, CultureInfo.InvariantCulture, out var headerValue) &&
        decimal.TryParse(body, NumberStyles.Float, CultureInfo.InvariantCulture, out var bodyValue) &&
        headerValue == decimal.Truncate(headerValue) && bodyValue == decimal.Truncate(bodyValue) &&
        Math.Abs(headerValue) <= MaxSafeInteger && Math.Abs(bodyValue) <= MaxSafeInteger && headerValue == bodyValue;

    private static void Reject(string message) => throw new McpProtocolException(message, McpErrorCode.HeaderMismatch);
}