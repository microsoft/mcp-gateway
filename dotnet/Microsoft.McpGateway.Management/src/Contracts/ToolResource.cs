// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Microsoft.McpGateway.Management.Contracts
{
    /// <summary>
    /// Represents a tool resource with metadata.
    /// </summary>
    public class ToolResource : ToolData
    {
        [JsonPropertyOrder(-1)]
        [JsonPropertyName("id")]
        public required string Id { get; set; }

        /// <summary>
        /// The ID of the user who created the tool.
        /// </summary>
        [JsonPropertyOrder(30)]
        public required string CreatedBy { get; set; }

        /// <summary>
        /// The date and time when the tool was created.
        /// </summary>
        [JsonPropertyOrder(31)]
        public DateTimeOffset CreatedAt { get; set; }

        /// <summary>
        /// The date and time when the tool was last updated.
        /// </summary>
        [JsonPropertyOrder(32)]
        public DateTimeOffset LastUpdatedAt { get; set; }

        public static ToolResource Create(ToolData data, string createdBy, DateTimeOffset createdAt) =>
            new()
            {
                Id = data.Name,
                Name = data.Name,
                ImageName = data.ImageName,
                ImageVersion = data.ImageVersion,
                EnvironmentVariables = data.EnvironmentVariables,
                ReplicaCount = data.ReplicaCount,
                Description = data.Description,
                UseWorkloadIdentity = data.UseWorkloadIdentity,
                ToolDefinition = data.ToolDefinition,
                CreatedBy = createdBy,
                CreatedAt = createdAt,
                LastUpdatedAt = DateTimeOffset.UtcNow,
            };

        public ToolResource() { }
    }
}
