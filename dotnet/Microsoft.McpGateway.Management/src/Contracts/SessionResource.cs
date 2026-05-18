// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Linq;
using System.Text.Json.Serialization;

namespace Microsoft.McpGateway.Management.Contracts
{
    /// <summary>
    /// Represents a session: one execution of an agent definition.
    /// The agent definition is snapshotted at create time so that later edits
    /// to the agent do not affect in-flight or replayed sessions.
    /// </summary>
    public class SessionResource : SessionData, IManagedResource
    {
        [JsonPropertyOrder(-1)]
        [JsonPropertyName("id")]
        public required string Id { get; set; }

        /// <summary>
        /// Resource identifier exposed to <see cref="IManagedResource"/> consumers
        /// (permission checks, logging). Sessions identify themselves by their Id.
        /// </summary>
        [JsonIgnore]
        string IManagedResource.Name => Id;

        /// <summary>
        /// Snapshot of the agent definition at the moment the session was created.
        /// </summary>
        [JsonPropertyOrder(10)]
        public required AgentResource AgentSnapshot { get; set; }

        /// <summary>
        /// Current lifecycle state.
        /// </summary>
        [JsonPropertyOrder(11)]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public SessionStatus Status { get; set; } = SessionStatus.Pending;

        /// <summary>
        /// Optional final result text (set when status reaches Completed).
        /// </summary>
        [JsonPropertyOrder(12)]
        public string? Result { get; set; }

        /// <summary>
        /// Optional error message (set when status reaches Failed).
        /// </summary>
        [JsonPropertyOrder(13)]
        public string? Error { get; set; }

        /// <summary>
        /// Conversation history accumulated by the agent loop. Append-only.
        /// Used by future multi-turn endpoints to continue this session.
        /// </summary>
        [JsonPropertyOrder(14)]
        public List<SessionMessage> Messages { get; set; } = new();

        /// <summary>
        /// Per-session working directory for built-in tools (bash / read_file /
        /// write_file). Reserved hook so future sandbox can swap underlying
        /// storage without changing tool callsites. May be null until
        /// built-in tools are introduced.
        /// </summary>
        [JsonPropertyOrder(15)]
        public string? WorkingDirectory { get; set; }

        /// <summary>
        /// Id of the parent session that spawned this one (subagent /
        /// invoke-agent pattern). Null for top-level sessions.
        /// </summary>
        [JsonPropertyOrder(16)]
        public string? ParentSessionId { get; set; }

        [JsonPropertyOrder(30)]
        public required string CreatedBy { get; set; }

        [JsonPropertyOrder(31)]
        public DateTimeOffset CreatedAt { get; set; }

        [JsonPropertyOrder(32)]
        public DateTimeOffset LastUpdatedAt { get; set; }

        public static SessionResource Create(SessionData data, AgentResource agentSnapshot, string createdBy, DateTimeOffset createdAt) =>
            new()
            {
                Id = $"sess-{Guid.NewGuid():N}",
                AgentName = data.AgentName,
                Input = data.Input,
                Title = string.IsNullOrWhiteSpace(data.Title) ? data.AgentName : data.Title,
                RequiredRoles = data.RequiredRoles?.ToList() ?? [],
                AgentSnapshot = agentSnapshot,
                Status = SessionStatus.Pending,
                Messages = new List<SessionMessage>(),
                CreatedBy = createdBy,
                CreatedAt = createdAt,
                LastUpdatedAt = DateTimeOffset.UtcNow,
            };

        public SessionResource() { }
    }
}
