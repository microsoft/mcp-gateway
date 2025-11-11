// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text;
using System.Text.Json;
using Microsoft.McpGateway.Management.Authorization;
using Microsoft.McpGateway.Management.Store;
using Microsoft.McpGateway.Tools.Contracts;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Microsoft.McpGateway.Tools.Services
{
    /// <summary>
    /// Implementation that forwards tool execution to the inference server endpoint.
    /// </summary>
    /// <remarks>
    /// Initializes a new instance of the <see cref="HttpToolExecutor"/> class.
    /// </remarks>
    public class HttpToolExecutor(
        IHttpClientFactory httpClientFactory,
        IToolDefinitionProvider toolDefinitionProvider,
        IToolResourceStore toolResourceStore,
        IPermissionProvider permissionProvider,
        IHttpContextAccessor httpContextAccessor,
        ILogger<HttpToolExecutor> logger) : IToolExecutor
    {
        private readonly IHttpClientFactory httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        private readonly ILogger<HttpToolExecutor> logger = logger ?? throw new ArgumentNullException(nameof(logger));
        private readonly IToolDefinitionProvider toolDefinitionProvider = toolDefinitionProvider ?? throw new ArgumentNullException(nameof(toolDefinitionProvider));
        private readonly IToolResourceStore toolResourceStore = toolResourceStore ?? throw new ArgumentNullException(nameof(toolResourceStore));
        private readonly IPermissionProvider permissionProvider = permissionProvider ?? throw new ArgumentNullException(nameof(permissionProvider));
        private readonly IHttpContextAccessor httpContextAccessor = httpContextAccessor ?? throw new ArgumentNullException(nameof(httpContextAccessor));

        /// <inheritdoc/>
        public async ValueTask<CallToolResult> ExecuteToolAsync(
            RequestContext<CallToolRequestParams> requestContext,
            CancellationToken cancellationToken)
        {
            var toolName = requestContext.Params?.Name;
            if (string.IsNullOrEmpty(toolName))
            {
                this.logger.LogError("Tool name is null or empty");
                return new CallToolResult
                {
                    Content =
                    [
                        new TextContentBlock { Text = "Error: Tool name is required" }
                    ],
                    IsError = true
                };
            }

            this.logger.LogInformation("Executing tool: {ToolName}", toolName);

            try
            {
                var principal = this.httpContextAccessor.HttpContext?.User;
                if (principal == null)
                {
                    this.logger.LogWarning("Missing authenticated user context while executing tool {ToolName}", toolName);
                    return CreateErrorResult($"Error: User context is required to execute tool '{toolName}'.");
                }

                var toolResource = await this.toolResourceStore.TryGetAsync(toolName, cancellationToken).ConfigureAwait(false);
                if (toolResource == null)
                {
                    this.logger.LogWarning("Tool not found while executing: {ToolName}", toolName);
                    return CreateErrorResult($"Error: Tool '{toolName}' not found");
                }

                if (!await this.permissionProvider.CheckAccessAsync(principal, toolResource, Operation.Read).ConfigureAwait(false))
                {
                    this.logger.LogWarning("User {UserId} denied read access for tool {ToolName}", principal.Identity?.Name, toolName);
                    return CreateErrorResult("Error: You do not have permission to execute this tool.");
                }

                var toolDefinition = toolResource.ToolDefinition;
                if (toolDefinition == null)
                {
                    this.logger.LogWarning("Tool definition missing for tool {ToolName}", toolName);
                    return CreateErrorResult($"Error: Tool '{toolName}' definition is unavailable.");
                }

                // Refresh provider cache for subsequent list requests
                _ = await this.toolDefinitionProvider.GetToolDefinitionAsync(toolName, cancellationToken).ConfigureAwait(false);

                // Compose the execution endpoint URL
                // Format: http://{toolName}-service.adapter.svc.cluster.local:{port}{path}
                var executionEndpoint = $"http://{toolName}-service.adapter.svc.cluster.local:{toolDefinition.Port}{toolDefinition.Path}";

                this.logger.LogInformation(
                    "Forwarding tool {ToolName} to endpoint: {Endpoint}",
                    toolName,
                    executionEndpoint);

                // Send request to inference server
                using var client = this.httpClientFactory.CreateClient();
                var jsonContent = new StringContent(
                    JsonSerializer.Serialize(requestContext.Params?.Arguments),
                    Encoding.UTF8,
                    "application/json");

                var response = await client.PostAsync(
                    executionEndpoint,
                    jsonContent,
                    cancellationToken).ConfigureAwait(false);

                // Handle response
                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                    this.logger.LogError(
                        "Inference server returned error for tool {ToolName}: {StatusCode} - {Error}",
                        toolName,
                        response.StatusCode,
                        errorContent);

                    return new CallToolResult
                    {
                        Content =
                        [
                            new TextContentBlock
                            {
                                Text = $"Error: Inference server returned {response.StatusCode}"
                            }
                        ],
                        IsError = true
                    };
                }

                // Parse and return successful response
                var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
                this.logger.LogInformation("Tool {ToolName} executed successfully", toolName);

                return new CallToolResult
                {
                    Content =
                    [
                        new TextContentBlock { Text = responseContent }
                    ]
                };
            }
            catch (HttpRequestException ex)
            {
                this.logger.LogError(ex, "HTTP error executing tool {ToolName}", toolName);
                return CreateErrorResult($"Error: Failed to connect to inference server - {ex.Message}");
            }
            catch (Exception ex)
            {
                this.logger.LogError(ex, "Unexpected error executing tool {ToolName}", toolName);
                return CreateErrorResult($"Error: {ex.Message}");
            }
        }

        private static CallToolResult CreateErrorResult(string message) => new()
        {
            Content =
            [
                new TextContentBlock { Text = message }
            ],
            IsError = true
        };
    }
}
