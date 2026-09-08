using System.Text;
using System.Text.Json;
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
}