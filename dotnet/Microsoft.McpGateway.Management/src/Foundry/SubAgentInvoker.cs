// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.McpGateway.Management.Contracts;
using Microsoft.McpGateway.Management.Store;

namespace Microsoft.McpGateway.Management.Foundry
{
    /// <summary>
    /// Spawns a child session against a peer agent and returns its final answer.
    /// Used to implement the <c>agent:</c> tool prefix: when a parent agent
    /// invokes a subagent function, this service drives the child through
    /// <see cref="AgentRunner"/> and persists it as a normal session with
    /// <c>ParentSessionId</c> set, so observers can navigate the call tree.
    /// </summary>
    public class SubAgentInvoker
    {
        // Hard cap on subagent recursion depth to avoid runaway loops where
        // one agent calls another that calls back. Counted via ParentSessionId
        // chain length at invocation time.
        private const int MaxDepth = 3;

        private readonly ISessionResourceStore _sessionStore;
        private readonly Func<AgentRunner> _runnerFactory;
        private readonly ILogger<SubAgentInvoker> _logger;

        public SubAgentInvoker(
            ISessionResourceStore sessionStore,
            Func<AgentRunner> runnerFactory,
            ILogger<SubAgentInvoker> logger)
        {
            _sessionStore = sessionStore ?? throw new ArgumentNullException(nameof(sessionStore));
            _runnerFactory = runnerFactory ?? throw new ArgumentNullException(nameof(runnerFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Run <paramref name="childAgent"/> as a fresh session whose
        /// <c>ParentSessionId</c> points back to <paramref name="parentSessionId"/>.
        /// Returns a <see cref="ToolResult"/> whose content is the child's
        /// final answer (or a JSON error blob if the run failed).
        /// </summary>
        /// <param name="accessContext">
        /// The effective caller of the originating run. Carried into the child
        /// run so the child's own nested tools/subagents are authorized against
        /// the live caller rather than the parent's creator (MSRC-122743).
        /// </param>
        public async Task<ToolResult> InvokeAsync(
            AgentResource childAgent,
            string input,
            string parentSessionId,
            ClaimsPrincipal accessContext,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(childAgent);
            ArgumentException.ThrowIfNullOrEmpty(input);
            ArgumentException.ThrowIfNullOrEmpty(parentSessionId);
            ArgumentNullException.ThrowIfNull(accessContext);

            // Walk up the parent chain to enforce depth limit.
            var depth = await ComputeDepthAsync(parentSessionId, cancellationToken).ConfigureAwait(false);
            if (depth >= MaxDepth)
            {
                _logger.LogWarning("Refusing to spawn subagent {agent}: depth {depth} >= {max} from parent {parent}.",
                    childAgent.Name, depth, MaxDepth, parentSessionId);
                return new ToolResult(
                    JsonSerializer.Serialize(new { error = $"Subagent depth limit ({MaxDepth}) reached." }),
                    IsError: true);
            }

            // The child session is recorded under the parent's creator for
            // lineage/observability, but authorization for the child's nested
            // resources uses the live caller (accessContext), not this value.
            var parent = await _sessionStore.TryGetAsync(parentSessionId, cancellationToken).ConfigureAwait(false);
            var createdBy = parent?.CreatedBy ?? "subagent";

            var data = new SessionData
            {
                AgentName = childAgent.Name,
                Input = input,
                Title = $"subagent({childAgent.Name})",
            };
            var child = SessionResource.Create(data, childAgent, createdBy, DateTimeOffset.UtcNow);
            child.ParentSessionId = parentSessionId;
            child.Status = SessionStatus.Running;
            // Each subagent gets its own working directory so its file ops are
            // isolated from the parent's. P2 will tighten with a real sandbox.
            child.WorkingDirectory = Path.Combine(Path.GetTempPath(), "agent-sessions", child.Id);
            Directory.CreateDirectory(child.WorkingDirectory);
            await _sessionStore.UpsertAsync(child, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Spawned subagent session {child} for parent {parent} (agent={agent}, depth={depth}).",
                child.Id, parentSessionId, childAgent.Name, depth);

            string answer = string.Empty;
            string? error = null;
            var runner = _runnerFactory();
            await foreach (var evt in runner.RunStreamingAsync(
                childAgent,
                input,
                child.Id,
                parentSessionId: parentSessionId,
                history: child.Messages,
                workingDirectory: child.WorkingDirectory,
                accessContext,
                cancellationToken).ConfigureAwait(false))
            {
                if (evt.Type == SessionEventType.Completed)
                {
                    answer = evt.Answer ?? string.Empty;
                    child.Status = SessionStatus.Completed;
                    child.Result = answer;
                }
                else if (evt.Type == SessionEventType.Failed)
                {
                    error = evt.Error;
                    child.Status = SessionStatus.Failed;
                    child.Error = error;
                }
            }
            child.LastUpdatedAt = DateTimeOffset.UtcNow;
            await _sessionStore.UpsertAsync(child, CancellationToken.None).ConfigureAwait(false);

            if (error != null)
            {
                return new ToolResult(
                    JsonSerializer.Serialize(new { error, childSessionId = child.Id }),
                    IsError: true);
            }
            return new ToolResult(answer, IsError: false);
        }

        private async Task<int> ComputeDepthAsync(string parentSessionId, CancellationToken cancellationToken)
        {
            var depth = 0;
            var cursor = parentSessionId;
            while (!string.IsNullOrEmpty(cursor) && depth < MaxDepth + 1)
            {
                var session = await _sessionStore.TryGetAsync(cursor, cancellationToken).ConfigureAwait(false);
                if (session?.ParentSessionId == null)
                {
                    break;
                }
                depth++;
                cursor = session.ParentSessionId;
            }
            return depth;
        }
    }
}
