using System.Text;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Microsoft.McpGateway.Management.Contracts;

public static class McpProtocol
{
    public const string Version = "2026-07-28";
    public const string VersionHeader = "MCP-Protocol-Version";
    private const string Base64Prefix = "=?base64?";
    private const string Base64Suffix = "?=";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static string DecodeHeader(string value)
    {
        if (value != value.Trim() || value.Any(character => character != '\t' && (character < ' ' || character > '~')))
            throw new McpProtocolException("Mirrored headers must use ASCII or the Base64 sentinel encoding.", McpErrorCode.HeaderMismatch);

        if (!value.StartsWith(Base64Prefix, StringComparison.Ordinal) || !value.EndsWith(Base64Suffix, StringComparison.Ordinal))
            return value;

        // "=?base64?=" satisfies both tests with the delimiters overlapping, so the
        // payload slice below would be a negative length. Reject it as a malformed
        // sentinel rather than letting it escape as an unhandled exception.
        if (value.Length < Base64Prefix.Length + Base64Suffix.Length)
            throw new McpProtocolException("Invalid Base64 mirrored header.", McpErrorCode.HeaderMismatch);

        try
        {
            return StrictUtf8.GetString(Convert.FromBase64String(value[Base64Prefix.Length..^Base64Suffix.Length]));
        }
        catch (Exception exception) when (exception is FormatException or DecoderFallbackException)
        {
            throw new McpProtocolException("Invalid Base64 mirrored header.", McpErrorCode.HeaderMismatch);
        }
    }
}