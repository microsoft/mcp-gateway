// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.McpGateway.Management.Store;
using ModelContextProtocol.Protocol;

namespace Microsoft.McpGateway.Service.Mcp
{
    /// <summary>
    /// Service that aggregates MCP tools from all registered adapters.
    /// </summary>
    public class McpAggregatorService : IMcpAggregatorService
    {
        private readonly IAdapterResourceStore _adapterStore;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<McpAggregatorService> _logger;

        public McpAggregatorService(
            IAdapterResourceStore adapterStore,
            IHttpClientFactory httpClientFactory,
            ILogger<McpAggregatorService> logger)
        {
            _adapterStore = adapterStore ?? throw new ArgumentNullException(nameof(adapterStore));
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<Tool>> ListAllToolsAsync(CancellationToken cancellationToken = default)
        {
            var allTools = new List<Tool>();
            var adapters = (await _adapterStore.ListAsync(cancellationToken).ConfigureAwait(false)).ToList();

            _logger.LogInformation("Aggregating tools from {Count} adapters", adapters.Count);

            var tasks = adapters.Select(async adapter =>
            {
                try
                {
                    var tools = await DiscoverToolsFromAdapterAsync(adapter.Name, cancellationToken).ConfigureAwait(false);
                    // Prefix tool names with adapter name to make them unique
                    return tools.Select(t => new Tool
                    {
                        Name = $"{adapter.Name}-{t.Name}",
                        Description = t.Description,
                        InputSchema = t.InputSchema
                    }).ToList();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to discover tools from adapter {AdapterName}", adapter.Name);
                    return new List<Tool>();
                }
            });

            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            foreach (var tools in results)
            {
                allTools.AddRange(tools);
            }

            _logger.LogInformation("Aggregated {Count} total tools from all adapters", allTools.Count);
            return allTools;
        }

        /// <inheritdoc />
        public (string adapterName, string toolName) ParseToolName(string aggregatedToolName)
        {
            // Tool name format is "{adapterName}-{originalToolName}"
            // Since adapter names can contain dashes, we need to find the longest matching adapter prefix
            // For example: "mcp-everything-echo" should parse as ("mcp-everything", "echo")
            
            var adapters = _adapterStore.ListAsync(CancellationToken.None).GetAwaiter().GetResult();
            
            foreach (var adapter in adapters.OrderByDescending(a => a.Name.Length))
            {
                var prefix = adapter.Name + "-";
                if (aggregatedToolName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return (adapter.Name, aggregatedToolName.Substring(prefix.Length));
                }
            }
            
            throw new ArgumentException($"No matching adapter found for tool: {aggregatedToolName}");
        }

        private async Task<IReadOnlyList<Tool>> DiscoverToolsFromAdapterAsync(string adapterName, CancellationToken cancellationToken)
        {
            using var client = _httpClientFactory.CreateClient("McpAggregator");

            // Initialize MCP session
            var initRequest = new
            {
                jsonrpc = "2.0",
                method = "initialize",
                @params = new
                {
                    protocolVersion = "2024-11-05",
                    capabilities = new { },
                    clientInfo = new { name = "MCP Gateway Aggregator", version = "1.0.0" }
                },
                id = 1
            };

            using var initHttpRequest = new HttpRequestMessage(HttpMethod.Post, $"/adapters/{adapterName}/mcp");
            initHttpRequest.Headers.Add("Accept", "text/event-stream, application/json");
            initHttpRequest.Content = JsonContent.Create(initRequest);

            var initResponse = await client.SendAsync(initHttpRequest, cancellationToken).ConfigureAwait(false);
            if (!initResponse.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to initialize session with adapter {AdapterName}: {Status}", adapterName, initResponse.StatusCode);
                return Array.Empty<Tool>();
            }

            var sessionId = initResponse.Headers.TryGetValues("mcp-session-id", out var ids) ? ids.FirstOrDefault() : null;
            if (string.IsNullOrEmpty(sessionId))
            {
                _logger.LogWarning("No session ID from adapter {AdapterName}", adapterName);
                return Array.Empty<Tool>();
            }

            // Send initialized notification
            var initializedNotification = new
            {
                jsonrpc = "2.0",
                method = "notifications/initialized",
                @params = new { }
            };

            using var notifyRequest = new HttpRequestMessage(HttpMethod.Post, $"/adapters/{adapterName}/mcp");
            notifyRequest.Headers.Add("Accept", "text/event-stream, application/json");
            notifyRequest.Headers.Add("mcp-session-id", sessionId);
            notifyRequest.Content = JsonContent.Create(initializedNotification);
            await client.SendAsync(notifyRequest, cancellationToken).ConfigureAwait(false);

            // List tools
            var listToolsRequest = new
            {
                jsonrpc = "2.0",
                method = "tools/list",
                @params = new { },
                id = 2
            };

            using var toolsHttpRequest = new HttpRequestMessage(HttpMethod.Post, $"/adapters/{adapterName}/mcp");
            toolsHttpRequest.Headers.Add("Accept", "text/event-stream, application/json");
            toolsHttpRequest.Headers.Add("mcp-session-id", sessionId);
            toolsHttpRequest.Content = JsonContent.Create(listToolsRequest);

            var toolsResponse = await client.SendAsync(toolsHttpRequest, cancellationToken).ConfigureAwait(false);
            if (!toolsResponse.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to list tools from adapter {AdapterName}: {Status}", adapterName, toolsResponse.StatusCode);
                return Array.Empty<Tool>();
            }

            var content = await toolsResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("Tools response from adapter {AdapterName}: {Content}", adapterName, content.Length > 500 ? content.Substring(0, 500) + "..." : content);
            return ParseToolsFromSseResponse(content);
        }

        private List<Tool> ParseToolsFromSseResponse(string sseResponse)
        {
            var tools = new List<Tool>();
            _logger.LogDebug("Parsing SSE response: {Response}", sseResponse.Length > 200 ? sseResponse.Substring(0, 200) + "..." : sseResponse);
            
            foreach (var line in sseResponse.Split('\n'))
            {
                if (!line.StartsWith("data: "))
                    continue;

                var jsonData = line.Substring(6);
                try
                {
                    using var doc = JsonDocument.Parse(jsonData);
                    if (doc.RootElement.TryGetProperty("result", out var result) &&
                        result.TryGetProperty("tools", out var toolsArray))
                    {
                        foreach (var toolElement in toolsArray.EnumerateArray())
                        {
                            var tool = JsonSerializer.Deserialize<Tool>(toolElement.GetRawText());
                            if (tool != null)
                                tools.Add(tool);
                        }
                    }
                }
                catch (JsonException)
                {
                    // Skip malformed JSON
                }
            }
            return tools;
        }
    }
}
