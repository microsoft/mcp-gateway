// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace Microsoft.McpGateway.Management.Foundry
{
    /// <summary>
    /// Discriminator for <see cref="SessionEvent"/>. Serialized as the SSE
    /// <c>event:</c> field and as the <c>type</c> property in the JSON payload.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum SessionEventType
    {
        /// <summary>The agent run has begun. Emitted exactly once.</summary>
        Started,
        /// <summary>The model issued a tool invocation. Carries tool name + arguments.</summary>
        ToolCallStarted,
        /// <summary>A previously started tool call returned. Carries result text.</summary>
        ToolCallCompleted,
        /// <summary>The agent produced a final assistant message. Emitted at most once.</summary>
        Completed,
        /// <summary>The run terminated with an error. Emitted at most once.</summary>
        Failed,
        /// <summary>
        /// Incremental assistant text token(s). Emitted zero or more times
        /// before <see cref="Completed"/>; the concatenation of all
        /// <see cref="SessionEvent.DeltaText"/> values for an iteration
        /// equals the <see cref="SessionEvent.Answer"/> in <see cref="Completed"/>.
        /// </summary>
        TokenDelta,
    }

    /// <summary>
    /// One event emitted by <see cref="AgentRunner.RunStreamingAsync"/>.
    /// Designed to be written as an SSE message: <c>event: {Type}\ndata: {JSON}\n\n</c>.
    /// </summary>
    public class SessionEvent
    {
        [JsonPropertyName("type")]
        public required SessionEventType Type { get; set; }

        [JsonPropertyName("sessionId")]
        public required string SessionId { get; set; }

        /// <summary>
        /// Id of the parent session that spawned this run (subagent /
        /// invoke-agent pattern). Null for top-level runs. Reserved hook so
        /// clients can already begin to render call-trees.
        /// </summary>
        [JsonPropertyName("parentSessionId")]
        public string? ParentSessionId { get; set; }

        /// <summary>Iteration index (0-based) when the event was produced.</summary>
        [JsonPropertyName("iteration")]
        public int Iteration { get; set; }

        /// <summary>For ToolCallStarted/ToolCallCompleted.</summary>
        [JsonPropertyName("toolCallId")]
        public string? ToolCallId { get; set; }

        /// <summary>For ToolCallStarted/ToolCallCompleted.</summary>
        [JsonPropertyName("toolName")]
        public string? ToolName { get; set; }

        /// <summary>For ToolCallStarted: JSON arguments the model passed.</summary>
        [JsonPropertyName("arguments")]
        public string? Arguments { get; set; }

        /// <summary>For ToolCallCompleted: tool response body (typically JSON).</summary>
        [JsonPropertyName("result")]
        public string? Result { get; set; }

        /// <summary>For Completed: the assistant's final text answer.</summary>
        [JsonPropertyName("answer")]
        public string? Answer { get; set; }

        /// <summary>For Failed: human-readable error.</summary>
        [JsonPropertyName("error")]
        public string? Error { get; set; }

        /// <summary>
        /// For TokenDelta: the chunk of assistant text just produced by the
        /// model. Concatenate all deltas in an iteration to reconstruct the
        /// final answer.
        /// </summary>
        [JsonPropertyName("deltaText")]
        public string? DeltaText { get; set; }
    }
}
