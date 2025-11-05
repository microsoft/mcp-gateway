// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace Microsoft.McpGateway.Management.Contracts
{
    /// <summary>
    /// Represents the data for a tool deployment, including image details and the tool definition.
    /// </summary>
    public class ToolData : AdapterData
    {
        /// <summary>
        /// The MCP tool definition as JSON.
        /// This contains the tool's name, description, input schema, etc.
        /// </summary>
        [JsonPropertyOrder(10)]
        public required ToolDefinition ToolDefinition { get; set; }

        public ToolData(
            string name,
            string imageName,
            string imageVersion,
            ToolDefinition toolDefinition,
            Dictionary<string, string>? environmentVariables = null,
            int? replicaCount = 1,
            string description = "",
            bool useWorkloadIdentity = false)
            : base(name, imageName, imageVersion, environmentVariables, replicaCount, description, useWorkloadIdentity)
        {
            if (name != toolDefinition.Name)
            {
                throw new ArgumentException("Tool name in ToolData must match the name in ToolDefinition.");
            }

            ToolDefinition = toolDefinition;
        }

        public ToolData() { }
    }
}
