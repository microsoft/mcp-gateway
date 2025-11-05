// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.McpGateway.Tools.Contracts;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Microsoft.McpGateway.Tools.Services
{
    /// <summary>
    /// Dummy executor that returns static responses.
    /// For testing and development purposes only.
    /// </summary>
    /// <remarks>
    /// Initializes a new instance of the <see cref="DummyToolExecutor"/> class.
    /// </remarks>
    public class DummyToolExecutor(ILogger<DummyToolExecutor> logger) : IToolExecutor
    {
        private readonly ILogger<DummyToolExecutor> logger = logger;

        /// <inheritdoc/>
        public ValueTask<CallToolResult> ExecuteToolAsync(
            RequestContext<CallToolRequestParams> requestContext,
            CancellationToken cancellationToken)
        {
            var toolName = requestContext.Params?.Name ?? "unknown";
            this.logger.LogInformation("Dummy execution of tool: {ToolName}", toolName);

            var result = new CallToolResult
            {
                Content =
                [
                    new TextContentBlock
                    {
                        Text = $"Dummy response for tool '{toolName}'"
                    }
                ]
            };
            
            return new ValueTask<CallToolResult>(result);
        }
    }
}
