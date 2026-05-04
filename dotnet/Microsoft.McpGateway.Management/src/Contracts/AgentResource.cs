// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Linq;
using System.Text.Json.Serialization;

namespace Microsoft.McpGateway.Management.Contracts
{
    /// <summary>
    /// Represents an agent resource with persistence metadata.
    /// </summary>
    public class AgentResource : AgentData, IManagedResource
    {
        [JsonPropertyOrder(-1)]
        [JsonPropertyName("id")]
        public required string Id { get; set; }

        /// <summary>
        /// The ID of the user who created the agent.
        /// </summary>
        [JsonPropertyOrder(30)]
        public required string CreatedBy { get; set; }

        /// <summary>
        /// The date and time when the agent was created.
        /// </summary>
        [JsonPropertyOrder(31)]
        public DateTimeOffset CreatedAt { get; set; }

        /// <summary>
        /// The date and time when the agent was last updated.
        /// </summary>
        [JsonPropertyOrder(32)]
        public DateTimeOffset LastUpdatedAt { get; set; }

        public static AgentResource Create(AgentData data, string createdBy, DateTimeOffset createdAt) =>
            new()
            {
                Id = data.Name,
                Name = data.Name,
                Model = data.Model,
                System = data.System,
                Tools = data.Tools?.ToList() ?? [],
                Skills = data.Skills?.ToList() ?? [],
                Description = data.Description,
                Metadata = data.Metadata ?? [],
                RequiredRoles = data.RequiredRoles?.ToList() ?? [],
                CreatedBy = createdBy,
                CreatedAt = createdAt,
                LastUpdatedAt = DateTimeOffset.UtcNow,
            };

        public AgentResource() { }
    }
}
