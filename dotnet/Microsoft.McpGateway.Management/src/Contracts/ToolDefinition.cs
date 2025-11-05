// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;
using ModelContextProtocol.Protocol;

namespace Microsoft.McpGateway.Management.Contracts
{
    /// <summary>
    /// Represents an MCP tool definition.
    /// Combines the MCP Tool contract with execution metadata.
    /// </summary>
    public class ToolDefinition
    {
        /// <summary>
        /// The MCP Tool definition (contains name, description, input schema).
        /// </summary>
        [JsonPropertyName("tool")]
        public required Tool Tool { get; set; }

        /// <summary>
        /// The unique name of the tool (derived from Tool.Name for convenience).
        /// </summary>
        [JsonIgnore]
        public string Name => Tool.Name;

        /// <summary>
        /// The port for the execution service endpoint.
        /// Defaults to 443.
        /// </summary>
        [JsonPropertyName("port")]
        public int Port { get; set; } = 443;

        /// <summary>
        /// The path for the execution service endpoint.
        /// Defaults to "/score".
        /// </summary>
        [JsonPropertyName("path")]
        public string Path { get; set; } = "/score";
    }
}
