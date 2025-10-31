// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.McpGateway.Management.Contracts;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Microsoft.McpGateway.Tools.Contracts
{
    /// <summary>
    /// Service responsible for loading and providing MCP tool definitions from storage.
    /// </summary>
    public interface IToolDefinitionProvider
    {
        /// <summary>
        /// Loads all available tool definitions.
        /// </summary>
        Task<List<ToolDefinition>> GetToolDefinitionsAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets a specific tool definition by name.
        /// </summary>
        Task<ToolDefinition?> GetToolDefinitionAsync(string toolName, CancellationToken cancellationToken = default);

        /// <summary>
        /// MCP list tools handler.
        /// </summary>
        ValueTask<ListToolsResult> ListToolsAsync(RequestContext<ListToolsRequestParams> context, CancellationToken cancellationToken = default);
    }
}
