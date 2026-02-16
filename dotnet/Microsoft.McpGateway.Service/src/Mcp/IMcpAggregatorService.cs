// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using ModelContextProtocol.Protocol;

namespace Microsoft.McpGateway.Service.Mcp
{
    /// <summary>
    /// Service that aggregates MCP tools from all registered adapters.
    /// </summary>
    public interface IMcpAggregatorService
    {
        /// <summary>
        /// Lists all tools from all registered adapters.
        /// Tool names are prefixed with adapter name (e.g., "mcp-everything-echo").
        /// </summary>
        Task<IReadOnlyList<Tool>> ListAllToolsAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Parses an aggregated tool name into adapter name and original tool name.
        /// </summary>
        (string adapterName, string toolName) ParseToolName(string aggregatedToolName);
    }
}
