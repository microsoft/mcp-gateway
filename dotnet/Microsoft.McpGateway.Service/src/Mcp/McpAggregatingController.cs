// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.McpGateway.Management.Store;
using Microsoft.McpGateway.Service.Session;

namespace Microsoft.McpGateway.Service.Mcp
{
    /// <summary>
    /// Controller that serves as an aggregating MCP server.
    /// When clients connect to /mcp, this controller aggregates tools from all adapters.
    /// </summary>
    [ApiController]
    [Authorize]
    public class McpAggregatingController : ControllerBase
    {
        private readonly IMcpAggregatorService _aggregatorService;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IAdapterSessionStore _sessionStore;
        private readonly ILogger<McpAggregatingController> _logger;

        // Session state stored per mcp-session-id
        private static readonly Dictionary<string, AggregatorSession> _sessions = new();
        private static readonly object _sessionsLock = new();

        public McpAggregatingController(
            IMcpAggregatorService aggregatorService,
            IHttpClientFactory httpClientFactory,
            IAdapterSessionStore sessionStore,
            ILogger<McpAggregatingController> logger)
        {
            _aggregatorService = aggregatorService ?? throw new ArgumentNullException(nameof(aggregatorService));
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            _sessionStore = sessionStore ?? throw new ArgumentNullException(nameof(sessionStore));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Main MCP endpoint that aggregates tools from all adapters.
        /// </summary>
        [HttpPost("mcp")]
        public async Task HandleMcpRequest(CancellationToken cancellationToken)
        {
            // Read request body
            using var reader = new StreamReader(HttpContext.Request.Body);
            var body = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

            JsonDocument? requestDoc = null;
            try
            {
                requestDoc = JsonDocument.Parse(body);
            }
            catch (JsonException)
            {
                await WriteSseErrorAsync(-32700, "Parse error", null, cancellationToken).ConfigureAwait(false);
                return;
            }

            var root = requestDoc.RootElement;
            var method = root.TryGetProperty("method", out var m) ? m.GetString() : null;
            var id = root.TryGetProperty("id", out var i) ? i : (JsonElement?)null;
            var sessionId = HttpContext.Request.Headers["mcp-session-id"].FirstOrDefault();

            _logger.LogDebug("MCP request: method={Method}, sessionId={SessionId}", method, sessionId);

            switch (method)
            {
                case "initialize":
                    await HandleInitializeAsync(id, cancellationToken).ConfigureAwait(false);
                    break;

                case "notifications/initialized":
                    // No response needed for notifications
                    HttpContext.Response.StatusCode = 202;
                    break;

                case "tools/list":
                    await HandleToolsListAsync(id, cancellationToken).ConfigureAwait(false);
                    break;

                case "tools/call":
                    await HandleToolsCallAsync(root, id, sessionId, cancellationToken).ConfigureAwait(false);
                    break;

                default:
                    await WriteSseErrorAsync(-32601, $"Method not found: {method}", id, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }

        private async Task HandleInitializeAsync(JsonElement? id, CancellationToken cancellationToken)
        {
            var sessionId = Guid.NewGuid().ToString();

            lock (_sessionsLock)
            {
                _sessions[sessionId] = new AggregatorSession { CreatedAt = DateTime.UtcNow };
            }

            HttpContext.Response.Headers["mcp-session-id"] = sessionId;
            HttpContext.Response.ContentType = "text/event-stream";

            var result = new
            {
                protocolVersion = "2024-11-05",
                capabilities = new
                {
                    tools = new { }
                },
                serverInfo = new
                {
                    name = "MCP Gateway Aggregator",
                    version = "1.0.0"
                },
                instructions = "This gateway aggregates tools from multiple MCP servers. Tool names are prefixed with the adapter name (e.g., 'mcp-everything-echo')."
            };

            await WriteSseResultAsync(result, id, cancellationToken).ConfigureAwait(false);
        }

        private async Task HandleToolsListAsync(JsonElement? id, CancellationToken cancellationToken)
        {
            HttpContext.Response.ContentType = "text/event-stream";

            try
            {
                var tools = await _aggregatorService.ListAllToolsAsync(cancellationToken).ConfigureAwait(false);

                var result = new
                {
                    tools = tools.Select(t => new
                    {
                        name = t.Name,
                        description = t.Description,
                        inputSchema = t.InputSchema
                    })
                };

                await WriteSseResultAsync(result, id, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error listing tools");
                await WriteSseErrorAsync(-32603, "Internal error listing tools", id, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task HandleToolsCallAsync(JsonElement root, JsonElement? id, string? sessionId, CancellationToken cancellationToken)
        {
            HttpContext.Response.ContentType = "text/event-stream";

            if (!root.TryGetProperty("params", out var paramsEl) ||
                !paramsEl.TryGetProperty("name", out var nameEl))
            {
                await WriteSseErrorAsync(-32602, "Invalid params: missing tool name", id, cancellationToken).ConfigureAwait(false);
                return;
            }

            var aggregatedToolName = nameEl.GetString();
            if (string.IsNullOrEmpty(aggregatedToolName))
            {
                await WriteSseErrorAsync(-32602, "Invalid params: empty tool name", id, cancellationToken).ConfigureAwait(false);
                return;
            }

            try
            {
                var (adapterName, originalToolName) = _aggregatorService.ParseToolName(aggregatedToolName);

                // Forward the call to the appropriate adapter
                var arguments = paramsEl.TryGetProperty("arguments", out var args) ? args : (JsonElement?)null;

                var result = await ForwardToolCallAsync(adapterName, originalToolName, arguments, cancellationToken).ConfigureAwait(false);
                await WriteSseResultAsync(result, id, cancellationToken).ConfigureAwait(false);
            }
            catch (ArgumentException ex)
            {
                await WriteSseErrorAsync(-32602, ex.Message, id, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error calling tool {ToolName}", aggregatedToolName);
                await WriteSseErrorAsync(-32603, "Internal error calling tool", id, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task<object> ForwardToolCallAsync(string adapterName, string toolName, JsonElement? arguments, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Forwarding tool call: adapter={AdapterName}, tool={ToolName}", adapterName, toolName);
            using var client = _httpClientFactory.CreateClient("McpAggregator");

            // First, initialize a session with the adapter
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
            initHttpRequest.Content = System.Net.Http.Json.JsonContent.Create(initRequest);

            var initResponse = await client.SendAsync(initHttpRequest, cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("Init response: {StatusCode}", initResponse.StatusCode);
            if (!initResponse.IsSuccessStatusCode)
            {
                var errorContent = await initResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogError("Failed to initialize session with adapter {AdapterName}: {StatusCode} - {Content}", adapterName, initResponse.StatusCode, errorContent);
                throw new Exception($"Failed to initialize session with adapter {adapterName}: {initResponse.StatusCode}");
            }

            var adapterSessionId = initResponse.Headers.TryGetValues("mcp-session-id", out var ids) ? ids.FirstOrDefault() : null;
            if (string.IsNullOrEmpty(adapterSessionId))
            {
                throw new Exception($"No session ID from adapter {adapterName}");
            }

            // Now call the tool
            var callRequest = new
            {
                jsonrpc = "2.0",
                method = "tools/call",
                @params = new
                {
                    name = toolName,
                    arguments = arguments.HasValue ? JsonSerializer.Deserialize<object>(arguments.Value.GetRawText()) : null
                },
                id = 2
            };

            using var callHttpRequest = new HttpRequestMessage(HttpMethod.Post, $"/adapters/{adapterName}/mcp");
            callHttpRequest.Headers.Add("Accept", "text/event-stream, application/json");
            callHttpRequest.Headers.Add("mcp-session-id", adapterSessionId);
            callHttpRequest.Content = System.Net.Http.Json.JsonContent.Create(callRequest);

            var callResponse = await client.SendAsync(callHttpRequest, cancellationToken).ConfigureAwait(false);
            var content = await callResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("Tool call response: {StatusCode} - {Content}", callResponse.StatusCode, content.Length > 500 ? content.Substring(0, 500) : content);

            // Parse SSE response to get the result
            foreach (var line in content.Split('\n'))
            {
                if (!line.StartsWith("data: "))
                    continue;

                var jsonData = line.Substring(6);
                using var doc = JsonDocument.Parse(jsonData);
                if (doc.RootElement.TryGetProperty("result", out var result))
                {
                    return JsonSerializer.Deserialize<object>(result.GetRawText())!;
                }
                if (doc.RootElement.TryGetProperty("error", out var error))
                {
                    var errorMsg = error.TryGetProperty("message", out var msg) ? msg.GetString() : "Unknown error";
                    throw new Exception($"Tool error: {errorMsg}");
                }
            }

            throw new Exception("No result in tool call response");
        }

        private async Task WriteSseResultAsync(object result, JsonElement? id, CancellationToken cancellationToken)
        {
            var response = new
            {
                jsonrpc = "2.0",
                id = id.HasValue ? JsonSerializer.Deserialize<object>(id.Value.GetRawText()) : null,
                result
            };

            var json = JsonSerializer.Serialize(response);
            var eventId = $"{Guid.NewGuid()}_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";

            await HttpContext.Response.WriteAsync($"event: message\n", cancellationToken).ConfigureAwait(false);
            await HttpContext.Response.WriteAsync($"id: {eventId}\n", cancellationToken).ConfigureAwait(false);
            await HttpContext.Response.WriteAsync($"data: {json}\n\n", cancellationToken).ConfigureAwait(false);
            await HttpContext.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task WriteSseErrorAsync(int code, string message, JsonElement? id, CancellationToken cancellationToken)
        {
            HttpContext.Response.ContentType = "text/event-stream";

            var response = new
            {
                jsonrpc = "2.0",
                id = id.HasValue ? JsonSerializer.Deserialize<object>(id.Value.GetRawText()) : null,
                error = new { code, message }
            };

            var json = JsonSerializer.Serialize(response);
            var eventId = $"{Guid.NewGuid()}_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";

            await HttpContext.Response.WriteAsync($"event: message\n", cancellationToken).ConfigureAwait(false);
            await HttpContext.Response.WriteAsync($"id: {eventId}\n", cancellationToken).ConfigureAwait(false);
            await HttpContext.Response.WriteAsync($"data: {json}\n\n", cancellationToken).ConfigureAwait(false);
            await HttpContext.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        private class AggregatorSession
        {
            public DateTime CreatedAt { get; set; }
        }
    }
}
