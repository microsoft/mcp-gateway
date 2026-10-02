using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.McpGateway.Tools.Services;
using ModelContextProtocol;

namespace Microsoft.McpGateway.Tools.Tests;

[TestClass]
public class McpToolHeaderValidatorTests
{
    private const string StringSchema = """
        {"type":"object","properties":{"region":{"type":"string","x-mcp-header":"Region"}}}
        """;

    [DataTestMethod]
    [DataRow(null)]
    [DataRow("wrong-region")]
    [DataRow("=?base64?invalid!?=")]
    [DataRow("=?base64?=")]
    public void Validate_RejectsMissingOrMismatchedHeaders(string? header)
    {
        using var schema = JsonDocument.Parse(StringSchema);
        var arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("{\"region\":\"westus2\"}")!;
        var headers = new HeaderDictionary();
        if (header != null)
            headers["Mcp-Param-Region"] = header;

        Assert.ThrowsException<McpProtocolException>(() => McpToolHeaderValidator.Validate(schema.RootElement, arguments, headers));
    }

    [TestMethod]
    public void Validate_AcceptsMatchingNestedEncodedValue()
    {
        using var schema = JsonDocument.Parse("""
            {"type":"object","properties":{"settings":{"type":"object","properties":{"region":{"type":"string","x-mcp-header":"Region"}}}}}
            """);
        var arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("{\"settings\":{\"region\":\" westus2 \"}}")!;
        var headers = new HeaderDictionary
        {
            ["Mcp-Param-Region"] = "=?base64?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(" westus2 ")) + "?="
        };

        McpToolHeaderValidator.Validate(schema.RootElement, arguments, headers);
    }

    [DataTestMethod]
    [DataRow("42", true)]
    [DataRow("42.0", true)]
    [DataRow("4.2e1", true)]
    [DataRow("42.1", false)]
    [DataRow("9007199254740992", false)]
    public void Validate_ChecksIntegerHeadersNumerically(string header, bool valid)
    {
        using var schema = JsonDocument.Parse("""
            {"type":"object","properties":{"count":{"type":"integer","x-mcp-header":"Count"}}}
            """);
        var arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("{\"count\":42}")!;
        var headers = new HeaderDictionary { ["Mcp-Param-Count"] = header };
        if (valid)
            McpToolHeaderValidator.Validate(schema.RootElement, arguments, headers);
        else
            Assert.ThrowsException<McpProtocolException>(() => McpToolHeaderValidator.Validate(schema.RootElement, arguments, headers));
    }

    [TestMethod]
    public void Validate_AllowsAbsentOptionalArgumentButRejectsInventedHeader()
    {
        using var schema = JsonDocument.Parse(StringSchema);
        var arguments = new Dictionary<string, JsonElement>();
        var headers = new HeaderDictionary();
        McpToolHeaderValidator.Validate(schema.RootElement, arguments, headers);
        headers["Mcp-Param-Region"] = "westus2";
        Assert.ThrowsException<McpProtocolException>(() => McpToolHeaderValidator.Validate(schema.RootElement, arguments, headers));
    }

    [TestMethod]
    public void Validate_RejectsDuplicateHeaderValues()
    {
        using var schema = JsonDocument.Parse(StringSchema);
        var arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("{\"region\":\"westus2\"}")!;
        var headers = new HeaderDictionary { ["Mcp-Param-Region"] = new[] { "westus2", "westus2" } };
        Assert.ThrowsException<McpProtocolException>(() => McpToolHeaderValidator.Validate(schema.RootElement, arguments, headers));
    }

    [DataTestMethod]
    [DataRow("anyOf")]
    [DataRow("oneOf")]
    [DataRow("allOf")]
    [DataRow("items")]
    [DataRow("prefixItems")]
    [DataRow("additionalProperties")]
    [DataRow("$defs")]
    [DataRow("definitions")]
    [DataRow("patternProperties")]
    [DataRow("dependentSchemas")]
    public void Validate_RejectsAnnotationsInUnsupportedSchemaLocations(string keyword)
    {
        var annotated = JsonNode.Parse(StringSchema)!;
        JsonNode nested = keyword switch
        {
            "anyOf" or "oneOf" or "allOf" or "prefixItems" => new JsonArray(annotated),
            "$defs" or "definitions" or "patternProperties" or "dependentSchemas" => new JsonObject { ["definition"] = annotated },
            _ => annotated
        };
        var schema = new JsonObject { [keyword] = nested };
        if (keyword is "$defs" or "definitions")
            schema["$ref"] = $"#/{keyword}/definition";
        var definition = JsonSerializer.SerializeToElement(schema);

        Assert.ThrowsException<McpProtocolException>(() => McpToolHeaderValidator.Validate(definition, null, new HeaderDictionary()));
    }

    [DataTestMethod]
    [DataRow("{\"type\":\"string\",\"x-mcp-header\":\"Region\"}")]
    [DataRow("{\"properties\":{\"regions\":{\"type\":\"array\",\"items\":{\"type\":\"string\",\"x-mcp-header\":\"Region\"}}}}")]
    [DataRow("{\"$defs\":{\"properties\":{\"type\":\"string\",\"x-mcp-header\":\"Region\"}}}")]
    public void Validate_RejectsAnnotationsWithoutAnUnambiguousProperty(string json)
    {
        using var schema = JsonDocument.Parse(json);

        Assert.ThrowsException<McpProtocolException>(() => McpToolHeaderValidator.Validate(schema.RootElement, null, new HeaderDictionary()));
    }

    [DataTestMethod]
    [DataRow(null)]
    [DataRow("\"number\"")]
    [DataRow("\"object\"")]
    [DataRow("\"array\"")]
    [DataRow("[\"string\",\"integer\"]")]
    [DataRow("[\"null\"]")]
    public void Validate_RejectsUnsupportedAnnotationTypesEvenWhenArgumentIsAbsent(string? type)
    {
        var schema = JsonNode.Parse(StringSchema)!;
        var property = schema["properties"]!["region"]!.AsObject();
        if (type is null)
            property.Remove("type");
        else
            property["type"] = JsonNode.Parse(type);
        var definition = JsonSerializer.SerializeToElement(schema);

        Assert.ThrowsException<McpProtocolException>(() => McpToolHeaderValidator.Validate(definition, null, new HeaderDictionary()));
    }

    [DataTestMethod]
    [DataRow("string", "true", "true")]
    [DataRow("integer", "\"42\"", "42")]
    [DataRow("boolean", "\"true\"", "true")]
    public void Validate_RejectsArgumentTypeMismatch(string type, string argument, string header)
    {
        var schema = JsonNode.Parse(StringSchema)!;
        schema["properties"]!["region"]!["type"] = type;
        var definition = JsonSerializer.SerializeToElement(schema);
        using var value = JsonDocument.Parse(argument);
        var arguments = new Dictionary<string, JsonElement> { ["region"] = value.RootElement };
        var headers = new HeaderDictionary { ["Mcp-Param-Region"] = header };

        Assert.ThrowsException<McpProtocolException>(() => McpToolHeaderValidator.Validate(definition, arguments, headers));
    }

    [TestMethod]
    public void Validate_AcceptsNullablePrimitiveAndUnannotatedCompositions()
    {
        using var schema = JsonDocument.Parse("""
            {"allOf":[{"properties":{"other":{"type":"string"}}}],"properties":{"enabled":{"type":["boolean","null"],"x-mcp-header":"Enabled"}}}
            """);
        var arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("{\"enabled\":true}")!;
        var headers = new HeaderDictionary { ["Mcp-Param-Enabled"] = "true" };

        McpToolHeaderValidator.Validate(schema.RootElement, arguments, headers);
    }

    [DataTestMethod]
    [DataRow("default")]
    [DataRow("examples")]
    [DataRow("enum")]
    [DataRow("const")]
    public void Validate_DoesNotTreatInstanceDataAsSchema(string keyword)
    {
        var instance = new JsonObject { ["x-mcp-header"] = "not-an-annotation" };
        var schema = new JsonObject
        {
            [keyword] = keyword is "examples" or "enum" ? new JsonArray(instance) : instance
        };

        McpToolHeaderValidator.Validate(JsonSerializer.SerializeToElement(schema), null, new HeaderDictionary());
    }

    [TestMethod]
    public void Validate_RejectsDuplicateAnnotationNames()
    {
        using var schema = JsonDocument.Parse("""
            {"properties":{"first":{"type":"string","x-mcp-header":"Region"},"second":{"type":"string","x-mcp-header":"region"}}}
            """);

        Assert.ThrowsException<McpProtocolException>(() => McpToolHeaderValidator.Validate(schema.RootElement, null, new HeaderDictionary()));
    }
}