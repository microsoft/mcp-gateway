// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace Microsoft.McpGateway.Management.Contracts
{
    /// <summary>
    /// One message in a session's conversation history. Provider-agnostic
    /// (does not depend on OpenAI types) so it survives round-trip through
    /// Cosmos and any future model swap.
    /// </summary>
    public class SessionMessage
    {
        [JsonPropertyName("role")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public SessionMessageRole Role { get; set; }

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;

        /// <summary>For Tool messages: id of the tool_call this message answers.</summary>
        [JsonPropertyName("toolCallId")]
        public string? ToolCallId { get; set; }

        /// <summary>For Tool messages: name of the tool that produced this content.</summary>
        [JsonPropertyName("toolName")]
        public string? ToolName { get; set; }
    }

    public enum SessionMessageRole
    {
        System,
        User,
        Assistant,
        Tool,
    }
}
