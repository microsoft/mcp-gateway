// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.McpGateway.Management.Contracts
{
    /// <summary>
    /// Defines the type of MCP resource being deployed.
    /// </summary>
    public enum ResourceType
    {
        /// <summary>
        /// Represents an adapter resource.
        /// </summary>
        Mcp,

        /// <summary>
        /// Represents a tool resource.
        /// </summary>
        Tool
    }
}
