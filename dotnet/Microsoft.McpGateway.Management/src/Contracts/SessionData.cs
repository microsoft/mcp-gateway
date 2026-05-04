// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text.Json.Serialization;

namespace Microsoft.McpGateway.Management.Contracts
{
    /// <summary>
    /// Represents the data submitted by a caller to start a session against an agent.
    /// </summary>
    public class SessionData
    {
        /// <summary>
        /// The name of the agent definition to execute.
        /// </summary>
        [JsonPropertyOrder(1)]
        [Required(AllowEmptyStrings = false)]
        [RegularExpression("^[a-z0-9-]+$", ErrorMessage = "AgentName must contain only lowercase letters, numbers and dashes.")]
        public required string AgentName { get; set; }

        /// <summary>
        /// The initial user task / prompt for this session.
        /// </summary>
        [JsonPropertyOrder(2)]
        [Required(AllowEmptyStrings = false)]
        public required string Input { get; set; }

        /// <summary>
        /// Optional human-readable title for the session.
        /// </summary>
        [JsonPropertyOrder(3)]
        public string Title { get; set; } = string.Empty;

        /// <summary>
        /// Optional list of roles allowed to access this session besides the creator and admins.
        /// </summary>
        [JsonPropertyOrder(4)]
        public IList<string> RequiredRoles { get; set; } = [];

        public SessionData(
            string agentName,
            string input,
            string title = "",
            IEnumerable<string>? requiredRoles = null)
        {
            ArgumentException.ThrowIfNullOrEmpty(agentName);
            ArgumentException.ThrowIfNullOrEmpty(input);

            AgentName = agentName;
            Input = input;
            Title = title;
            RequiredRoles = requiredRoles?.Where(static role => !string.IsNullOrWhiteSpace(role)).Select(static role => role.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? [];
        }

        public SessionData() { }
    }
}
