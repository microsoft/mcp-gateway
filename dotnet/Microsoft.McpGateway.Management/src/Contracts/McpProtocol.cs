using System.Text;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Microsoft.McpGateway.Management.Contracts;

public static class McpProtocol
{
    public const string Version = "2026-07-28";
    public const string VersionHeader = "MCP-Protocol-Version";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static string DecodeHeader(string value)
    {
        if (value != value.Trim() || value.Any(character => character != '\t' && (character < ' ' || character > '~')))
            throw new McpProtocolException("Mirrored headers must use ASCII or the Base64 sentinel encoding.", McpErrorCode.HeaderMismatch);

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
}