// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.McpGateway.Management.Authorization;
using Microsoft.McpGateway.Management.Contracts;
using Microsoft.McpGateway.Management.Store;

namespace Microsoft.McpGateway.Management.Foundry
{
    /// <summary>
    /// Resolves an agent's declared tool list against the resource stores
    /// (MCP tools, peer agents) and executes invocations either by HTTP
    /// (MCP) or by spawning a child session (subagent).
    /// </summary>
    public class AgentToolRegistry
    {
        // Tool names in AgentData are namespaced by prefix:
        //   "mcp:<name>"     → routed to an MCP tool pod
        //   "agent:<name>"   → spawn a child session against a peer agent (subagent / Task pattern)
        //   "builtin:<name>" → in-process built-ins implemented by BuiltinToolExecutor
        //                      (currently bash / read_file / write_file)
        // Unknown / unprefixed names are skipped with a log entry.
        private const string McpPrefix = "mcp:";
        private const string AgentPrefix = "agent:";
        private const string BuiltinPrefix = "builtin:";

        // OpenAI function names cannot contain ':' so we munge "agent:foo" →
        // "agent_foo" for the LLM-facing schema. Keep the prefix obvious so
        // the model knows it's invoking another agent, not a regular tool.
        private const string SubAgentFunctionPrefix = "agent_";

        private readonly IToolResourceStore _toolStore;
        private readonly IAgentResourceStore _agentStore;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IPermissionProvider _permissionProvider;
        private readonly SubAgentInvoker? _subAgentInvoker;
        private readonly BuiltinToolExecutor? _builtinExecutor;
        private readonly ILogger<AgentToolRegistry> _logger;

        public AgentToolRegistry(
            IToolResourceStore toolStore,
            IAgentResourceStore agentStore,
            IHttpClientFactory httpClientFactory,
            IPermissionProvider permissionProvider,
            ILogger<AgentToolRegistry> logger,
            SubAgentInvoker? subAgentInvoker = null,
            BuiltinToolExecutor? builtinExecutor = null)
        {
            _toolStore = toolStore ?? throw new ArgumentNullException(nameof(toolStore));
            _agentStore = agentStore ?? throw new ArgumentNullException(nameof(agentStore));
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            _permissionProvider = permissionProvider ?? throw new ArgumentNullException(nameof(permissionProvider));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _subAgentInvoker = subAgentInvoker;
            _builtinExecutor = builtinExecutor;
        }

        /// <summary>
        /// Resolve the agent's declared tool list. Returns successfully resolved
        /// tools; logs and skips any that are missing or use an unsupported prefix.
        /// </summary>
        /// <param name="accessContext">
        /// The effective caller of the current run. Each nested <c>mcp:</c> and
        /// <c>agent:</c> reference is re-authorized against its <em>current</em>
        /// <c>RequiredRoles</c> so that tools the caller may no longer access are
        /// never advertised to the model (MSRC-122743).
        /// </param>
        public async Task<IReadOnlyList<ResolvedTool>> ResolveAsync(IEnumerable<string> toolNames, ClaimsPrincipal accessContext, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(accessContext);
            var resolved = new List<ResolvedTool>();
            foreach (var raw in toolNames ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }
                if (raw.StartsWith(McpPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    var toolName = raw[McpPrefix.Length..];
                    var resource = await _toolStore.TryGetAsync(toolName, cancellationToken).ConfigureAwait(false);
                    if (resource?.ToolDefinition?.Tool == null)
                    {
                        _logger.LogWarning("MCP tool '{tool}' declared by agent but not found in store; skipping.", toolName);
                        continue;
                    }
                    if (!await _permissionProvider.CheckAccessAsync(accessContext, resource, Operation.Read).ConfigureAwait(false))
                    {
                        _logger.LogWarning("Caller lacks permission to invoke MCP tool '{tool}'; excluding from resolved tools.", toolName);
                        continue;
                    }
                    resolved.Add(new McpResolvedTool(toolName, resource));
                    continue;
                }
                if (raw.StartsWith(AgentPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    if (_subAgentInvoker == null)
                    {
                        _logger.LogWarning("Tool '{tool}' requires SubAgentInvoker but none is registered; skipping.", raw);
                        continue;
                    }
                    var agentName = raw[AgentPrefix.Length..];
                    var agent = await _agentStore.TryGetAsync(agentName, cancellationToken).ConfigureAwait(false);
                    if (agent == null)
                    {
                        _logger.LogWarning("Subagent '{agent}' declared by parent but not found in store; skipping.", agentName);
                        continue;
                    }
                    if (!await _permissionProvider.CheckAccessAsync(accessContext, agent, Operation.Read).ConfigureAwait(false))
                    {
                        _logger.LogWarning("Caller lacks permission to invoke subagent '{agent}'; excluding from resolved tools.", agentName);
                        continue;
                    }
                    resolved.Add(new SubAgentResolvedTool(SubAgentFunctionPrefix + Sanitize(agentName), agent));
                    continue;
                }
                if (raw.StartsWith(BuiltinPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    if (_builtinExecutor == null)
                    {
                        _logger.LogWarning("Tool '{tool}' requires BuiltinToolExecutor but none is registered; skipping.", raw);
                        continue;
                    }
                    var name = raw[BuiltinPrefix.Length..];
                    var fn = "builtin_" + name;
                    if (!BuiltinToolExecutor.SupportedKinds.Contains(fn, StringComparer.Ordinal))
                    {
                        _logger.LogWarning("Unknown builtin tool '{tool}'; supported: {kinds}.", raw, string.Join(",", BuiltinToolExecutor.SupportedKinds));
                        continue;
                    }
                    resolved.Add(new BuiltinResolvedTool(fn));
                    continue;
                }
                _logger.LogInformation("Skipping unprefixed tool '{tool}' (expected one of mcp:/agent:/builtin:).", raw);
            }
            return resolved;
        }

        /// <summary>
        /// Execute a tool call. Always returns a <see cref="ToolResult"/>
        /// (never throws for HTTP/agent failures) so the agent loop can feed
        /// errors back to the model for self-correction.
        /// </summary>
        /// <param name="parentSessionId">
        /// Session id of the agent making the call. Used to set
        /// <c>ParentSessionId</c> on any spawned subagent session and to
        /// inherit auth context for the child run.
        /// </param>
        /// <param name="accessContext">
        /// The effective caller of the current run. Nested <c>mcp:</c> and
        /// <c>agent:</c> references are re-authorized against their current
        /// <c>RequiredRoles</c> at invocation time, closing the window where a
        /// stale parent reference could execute a since-restricted resource
        /// (MSRC-122743).
        /// </param>
        public async Task<ToolResult> ExecuteAsync(ResolvedTool tool, string argumentsJson, string parentSessionId, string? workingDirectory, ClaimsPrincipal accessContext, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(tool);
            ArgumentNullException.ThrowIfNull(accessContext);
            return tool switch
            {
                McpResolvedTool mcp => await ExecuteMcpAsync(mcp, argumentsJson, accessContext, cancellationToken).ConfigureAwait(false),
                SubAgentResolvedTool sub => await ExecuteSubAgentAsync(sub, argumentsJson, parentSessionId, accessContext, cancellationToken).ConfigureAwait(false),
                BuiltinResolvedTool builtin => await ExecuteBuiltinAsync(builtin, argumentsJson, workingDirectory, cancellationToken).ConfigureAwait(false),
                _ => new ToolResult(JsonSerializer.Serialize(new { error = $"Unsupported tool kind '{tool.GetType().Name}'." }), IsError: true),
            };
        }

        private Task<ToolResult> ExecuteBuiltinAsync(BuiltinResolvedTool tool, string argumentsJson, string? workingDirectory, CancellationToken cancellationToken)
        {
            if (_builtinExecutor == null)
            {
                return Task.FromResult(new ToolResult("{\"error\":\"BuiltinToolExecutor is not registered.\"}", IsError: true));
            }
            if (string.IsNullOrWhiteSpace(workingDirectory))
            {
                return Task.FromResult(new ToolResult("{\"error\":\"Built-in tools require a session WorkingDirectory.\"}", IsError: true));
            }
            return _builtinExecutor.ExecuteAsync(tool.Name, argumentsJson, workingDirectory, cancellationToken);
        }

        private async Task<ToolResult> ExecuteMcpAsync(McpResolvedTool tool, string argumentsJson, ClaimsPrincipal accessContext, CancellationToken cancellationToken)
        {
            // Re-resolve and re-authorize against the tool's current state so a
            // stale parent reference cannot execute a since-restricted tool
            // (MSRC-122743). Roles may have been tightened after this run began.
            var current = await _toolStore.TryGetAsync(tool.Name, cancellationToken).ConfigureAwait(false);
            if (current?.ToolDefinition?.Tool == null)
            {
                _logger.LogWarning("MCP tool {tool} no longer exists at invocation time; denying.", tool.Name);
                return new ToolResult(JsonSerializer.Serialize(new { error = $"MCP tool '{tool.Name}' is no longer available." }), IsError: true);
            }
            if (!await _permissionProvider.CheckAccessAsync(accessContext, current, Operation.Read).ConfigureAwait(false))
            {
                _logger.LogWarning("Caller lacks permission to invoke MCP tool {tool} at invocation time; denying.", tool.Name);
                return new ToolResult(JsonSerializer.Serialize(new { error = "You do not have permission to invoke this tool." }), IsError: true);
            }

            var def = current.ToolDefinition;
            // Same convention as HttpToolExecutor: the tool's pod is reachable
            // via cluster DNS in the "adapter" namespace.
            var endpoint = $"http://{tool.Name}-service.adapter.svc.cluster.local:{def.Port}{def.Path}";

            _logger.LogInformation("Calling MCP tool {tool} at {endpoint} (args {len} bytes)", tool.Name, endpoint, argumentsJson?.Length ?? 0);

            using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(60);
            using var content = new StringContent(argumentsJson ?? "{}", Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(endpoint, content, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("MCP tool {tool} returned {code}: {body}", tool.Name, (int)response.StatusCode, body);
                return new ToolResult(
                    JsonSerializer.Serialize(new { error = $"HTTP {(int)response.StatusCode}", detail = body }),
                    IsError: true);
            }
            return new ToolResult(body, IsError: false);
        }

        private async Task<ToolResult> ExecuteSubAgentAsync(SubAgentResolvedTool tool, string argumentsJson, string parentSessionId, ClaimsPrincipal accessContext, CancellationToken cancellationToken)
        {
            if (_subAgentInvoker == null)
            {
                return new ToolResult("{\"error\":\"SubAgentInvoker is not registered.\"}", IsError: true);
            }

            // Re-resolve and re-authorize against the child agent's current
            // state so a stale parent reference cannot execute a since-restricted
            // child agent (MSRC-122743). The effective caller — not the parent's
            // creator — must satisfy the child's current RequiredRoles.
            var current = await _agentStore.TryGetAsync(tool.Agent.Name, cancellationToken).ConfigureAwait(false);
            if (current == null)
            {
                _logger.LogWarning("Subagent {agent} no longer exists at invocation time; denying.", tool.Agent.Name);
                return new ToolResult(JsonSerializer.Serialize(new { error = $"Subagent '{tool.Agent.Name}' is no longer available." }), IsError: true);
            }
            if (!await _permissionProvider.CheckAccessAsync(accessContext, current, Operation.Read).ConfigureAwait(false))
            {
                _logger.LogWarning("Caller lacks permission to invoke subagent {agent} at invocation time; denying.", tool.Agent.Name);
                return new ToolResult(JsonSerializer.Serialize(new { error = "You do not have permission to invoke this subagent." }), IsError: true);
            }

            string input;
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
                if (!doc.RootElement.TryGetProperty("input", out var inputProp) || inputProp.ValueKind != JsonValueKind.String)
                {
                    return new ToolResult(
                        JsonSerializer.Serialize(new { error = "Subagent call requires an 'input' string argument." }),
                        IsError: true);
                }
                input = inputProp.GetString() ?? string.Empty;
            }
            catch (JsonException ex)
            {
                return new ToolResult(
                    JsonSerializer.Serialize(new { error = $"Invalid JSON arguments: {ex.Message}" }),
                    IsError: true);
            }

            _logger.LogInformation("Invoking subagent {agent} from parent session {parent} (input {len} chars)",
                current.Name, parentSessionId, input.Length);
            return await _subAgentInvoker.InvokeAsync(current, input, parentSessionId, accessContext, cancellationToken).ConfigureAwait(false);
        }

        private static string Sanitize(string agentName)
        {
            // Function names allowed by OpenAI: ^[a-zA-Z0-9_-]{1,64}$
            // Agent names already match ^[a-z0-9-]+$, so just pass through.
            return agentName;
        }

        /// <summary>
        /// Build OpenAI function-tool descriptors for all resolved tools so the
        /// model can invoke them.
        /// </summary>
        public static List<global::OpenAI.Chat.ChatTool> BuildChatTools(IReadOnlyList<ResolvedTool> resolved)
        {
            var list = new List<global::OpenAI.Chat.ChatTool>();
            foreach (var tool in resolved)
            {
                switch (tool)
                {
                    case McpResolvedTool mcp:
                        {
                            var def = mcp.Resource.ToolDefinition.Tool;
                            BinaryData schema;
                            try
                            {
                                schema = BinaryData.FromString(JsonSerializer.Serialize(def.InputSchema));
                            }
                            catch
                            {
                                schema = BinaryData.FromString("{\"type\":\"object\",\"properties\":{}}");
                            }
                            list.Add(global::OpenAI.Chat.ChatTool.CreateFunctionTool(
                                functionName: mcp.Name,
                                functionDescription: def.Description ?? string.Empty,
                                functionParameters: schema));
                            break;
                        }
                    case SubAgentResolvedTool sub:
                        {
                            var description = string.IsNullOrWhiteSpace(sub.Agent.Description)
                                ? $"Delegate a task to the '{sub.Agent.Name}' agent."
                                : sub.Agent.Description;
                            var schema = BinaryData.FromString("{\"type\":\"object\",\"properties\":{\"input\":{\"type\":\"string\",\"description\":\"Task or question to delegate.\"}},\"required\":[\"input\"]}");
                            list.Add(global::OpenAI.Chat.ChatTool.CreateFunctionTool(
                                functionName: sub.Name,
                                functionDescription: description,
                                functionParameters: schema));
                            break;
                        }
                    case BuiltinResolvedTool builtin:
                        {
                            list.Add(BuiltinToolExecutor.BuildChatTool(builtin.Name));
                            break;
                        }
                }
            }
            return list;
        }
    }

    /// <summary>
    /// Discriminated base for any tool the agent loop can call.
    /// <see cref="Name"/> is the LLM-facing function name.
    /// </summary>
    public abstract record ResolvedTool(string Name);

    public sealed record McpResolvedTool(string Name, ToolResource Resource) : ResolvedTool(Name);

    public sealed record SubAgentResolvedTool(string Name, AgentResource Agent) : ResolvedTool(Name);

    /// <summary>
    /// In-process built-in tool (bash / read_file / write_file). <see cref="Name"/>
    /// matches one of <see cref="BuiltinToolExecutor.SupportedKinds"/>.
    /// </summary>
    public sealed record BuiltinResolvedTool(string Name) : ResolvedTool(Name);

    /// <summary>
    /// Outcome of a single tool invocation. <see cref="Content"/> is fed back
    /// to the model verbatim; <see cref="IsError"/> lets the agent loop and
    /// observers distinguish success from failure without parsing the body.
    /// </summary>
    public record ToolResult(string Content, bool IsError);
}
