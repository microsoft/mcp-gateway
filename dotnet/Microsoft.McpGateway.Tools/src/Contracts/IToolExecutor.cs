// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Microsoft.McpGateway.Tools.Contracts
{
    /// <summary>
    /// Service responsible for executing tool operations by delegating to the Tool Execution Service.
    /// </summary>
    public interface IToolExecutor
    {
        /// <summary>
        /// Executes a tool with the given arguments by calling the Tool Execution Service.
        /// </summary>
        ValueTask<CallToolResult> ExecuteToolAsync(RequestContext<CallToolRequestParams> requestContext, CancellationToken cancellationToken);
    }
}
