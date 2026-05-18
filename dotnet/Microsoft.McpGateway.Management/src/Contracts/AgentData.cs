// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text.Json.Serialization;

namespace Microsoft.McpGateway.Management.Contracts
{
    /// <summary>
    /// Represents the data for an agent definition. Agents are pure metadata
    /// (system prompt + model + tool list); unlike adapters/tools they do not
    /// run as a Kubernetes pod.
    /// </summary>
    public class AgentData
    {
        /// <summary>
        /// The name of the agent. Must contain only lowercase letters, numbers, and dashes.
        /// </summary>
        [JsonPropertyOrder(1)]
        [RegularExpression("^[a-z0-9-]+$", ErrorMessage = "Name must contain only lowercase letters, numbers and dashes.")]
        public required string Name { get; set; }

        /// <summary>
        /// The model identifier the agent should use (e.g. "claude-opus-4-7", "gpt-4o").
        /// </summary>
        [JsonPropertyOrder(2)]
        [Required(AllowEmptyStrings = false)]
        public required string Model { get; set; }

        /// <summary>
        /// The system prompt that defines the agent's behavior.
        /// </summary>
        [JsonPropertyOrder(3)]
        [Required(AllowEmptyStrings = false)]
        public required string System { get; set; }

        /// <summary>
        /// Names of tools the agent is allowed to call. Each entry must be
        /// prefixed with one of:
        /// <list type="bullet">
        ///   <item><description><c>mcp:&lt;tool-name&gt;</c> — a tool registered via <c>/tools</c>.</description></item>
        ///   <item><description><c>agent:&lt;agent-name&gt;</c> — a peer agent invoked as a subagent.</description></item>
        ///   <item><description><c>builtin:&lt;name&gt;</c> — in-process built-in (e.g. <c>builtin:bash</c>, <c>builtin:read_file</c>, <c>builtin:write_file</c>).</description></item>
        /// </list>
        /// Unprefixed entries are skipped at resolution time.
        /// </summary>
        [JsonPropertyOrder(4)]
        public IList<string> Tools { get; set; } = [];

        /// <summary>
        /// Optional skill identifiers (paths under aifd-workspace/) to mount.
        /// </summary>
        [JsonPropertyOrder(5)]
        public IList<string> Skills { get; set; } = [];

        /// <summary>
        /// A description of the agent.
        /// </summary>
        [JsonPropertyOrder(6)]
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// Free-form metadata (owner, domain, tags, ...).
        /// </summary>
        [JsonPropertyOrder(7)]
        public Dictionary<string, string> Metadata { get; set; } = [];

        /// <summary>
        /// Optional list of roles allowed to access this agent besides the creator and admins.
        /// </summary>
        [JsonPropertyOrder(8)]
        public IList<string> RequiredRoles { get; set; } = [];

        public AgentData(
            string name,
            string model,
            string system,
            IEnumerable<string>? tools = null,
            IEnumerable<string>? skills = null,
            string description = "",
            Dictionary<string, string>? metadata = null,
            IEnumerable<string>? requiredRoles = null)
        {
            ArgumentException.ThrowIfNullOrEmpty(name);
            ArgumentException.ThrowIfNullOrEmpty(model);
            ArgumentException.ThrowIfNullOrEmpty(system);

            Name = name;
            Model = model;
            System = system;
            Tools = tools?.ToList() ?? [];
            Skills = skills?.ToList() ?? [];
            Description = description;
            Metadata = metadata ?? [];
            RequiredRoles = requiredRoles?.Where(static role => !string.IsNullOrWhiteSpace(role)).Select(static role => role.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? [];
        }

        public AgentData() { }
    }
}
