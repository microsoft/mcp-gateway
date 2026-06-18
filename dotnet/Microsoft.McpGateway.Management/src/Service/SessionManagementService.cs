// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using Microsoft.Extensions.Logging;
using Microsoft.McpGateway.Management.Authorization;
using Microsoft.McpGateway.Management.Contracts;
using Microsoft.McpGateway.Management.Extensions;
using Microsoft.McpGateway.Management.Foundry;
using Microsoft.McpGateway.Management.Store;

namespace Microsoft.McpGateway.Management.Service
{
    /// <summary>
    /// Service for managing sessions. Drives an <see cref="AgentRunner"/>
    /// (LLM + tool calls + SSE event stream) and persists per-session state
    /// (status, messages, working directory) in the session store.
    /// Streaming requires <see cref="AgentRunner"/> to be registered, which
    /// happens only when <c>FoundrySettings:Endpoint</c> is configured.
    /// </summary>
    public class SessionManagementService : ISessionManagementService
    {
        private readonly ISessionResourceStore _store;
        private readonly IAgentResourceStore _agentStore;
        private readonly IPermissionProvider _permissionProvider;
        private readonly AgentRunner? _agentRunner;
        private readonly BuiltinToolExecutor? _builtinExecutor;
        private readonly ILogger _logger;

        public SessionManagementService(
            ISessionResourceStore store,
            IAgentResourceStore agentStore,
            IPermissionProvider permissionProvider,
            ILogger<SessionManagementService> logger,
            AgentRunner? agentRunner = null,
            BuiltinToolExecutor? builtinExecutor = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _agentStore = agentStore ?? throw new ArgumentNullException(nameof(agentStore));
            _permissionProvider = permissionProvider ?? throw new ArgumentNullException(nameof(permissionProvider));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _agentRunner = agentRunner;
            _builtinExecutor = builtinExecutor;
        }

        public async Task<SessionResource> CreateAsync(ClaimsPrincipal accessContext, SessionData request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(accessContext);
            ArgumentNullException.ThrowIfNull(request);

            var agent = await _agentStore.TryGetAsync(request.AgentName, cancellationToken).ConfigureAwait(false)
                ?? throw new ArgumentException($"Agent '{request.AgentName}' does not exist.");

            // Caller must be allowed to read the agent definition before invoking it.
            if (!await _permissionProvider.CheckAccessAsync(accessContext, agent, Operation.Read).ConfigureAwait(false))
            {
                _logger.LogWarning("User {userId} denied read access on agent {agent} when creating a session.", accessContext.GetUserId(), agent.Name.Sanitize());
                throw new UnauthorizedAccessException("You do not have permission to invoke this agent.");
            }

            var session = SessionResource.Create(request, agent, accessContext.GetUserId()!, DateTimeOffset.UtcNow);

            _logger.LogInformation("Creating /sessions/{id} for agent {agent}.", session.Id, agent.Name.Sanitize());
            await _store.UpsertAsync(session, cancellationToken).ConfigureAwait(false);

            // Phase 3: kick off the agent loop (LLM + tool calls) in the
            // background. Caller polls /sessions/{id} for completion. The
            // caller's principal is captured so nested tools/subagents are
            // authorized against the live caller (MSRC-122743).
            if (_agentRunner != null)
            {
                _ = Task.Run(() => RunSessionAsync(session.Id, accessContext), CancellationToken.None);
            }
            else
            {
                _logger.LogWarning("No AgentRunner registered; session {id} will stay in Pending state.", session.Id);
            }

            return session;
        }

        /// <summary>
        /// Background worker for a single session. Runs the agent's system prompt
        /// against the user's input as a one-shot chat completion. Updates the
        /// stored session with Status=Completed/Failed and Result/Error.
        /// </summary>
        private async Task RunSessionAsync(string sessionId, ClaimsPrincipal accessContext)
        {
            try
            {
                var session = await _store.TryGetAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
                if (session == null)
                {
                    _logger.LogWarning("Session {id} disappeared before background run could start.", sessionId);
                    return;
                }

                session.Status = SessionStatus.Running;
                session.LastUpdatedAt = DateTimeOffset.UtcNow;
                await _store.UpsertAsync(session, CancellationToken.None).ConfigureAwait(false);

                var result = await _agentRunner!.RunAsync(
                    session.AgentSnapshot,
                    session.Input,
                    accessContext,
                    CancellationToken.None).ConfigureAwait(false);

                session.Result = result;
                session.Status = SessionStatus.Completed;
                session.LastUpdatedAt = DateTimeOffset.UtcNow;
                await _store.UpsertAsync(session, CancellationToken.None).ConfigureAwait(false);
                _logger.LogInformation("Session {id} completed (result {len} chars).", sessionId, result.Length);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Session {id} failed during background run.", sessionId);
                try
                {
                    var session = await _store.TryGetAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
                    if (session != null)
                    {
                        session.Status = SessionStatus.Failed;
                        session.Error = ex.Message;
                        session.LastUpdatedAt = DateTimeOffset.UtcNow;
                        await _store.UpsertAsync(session, CancellationToken.None).ConfigureAwait(false);
                    }
                }
                catch (Exception writeEx)
                {
                    _logger.LogError(writeEx, "Failed to persist Failed status for session {id}.", sessionId);
                }
            }
        }

        public async IAsyncEnumerable<SessionEvent> RunStreamingAsync(
            ClaimsPrincipal accessContext,
            SessionData request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(accessContext);
            ArgumentNullException.ThrowIfNull(request);
            if (_agentRunner == null)
            {
                throw new InvalidOperationException("Streaming runs require an AgentRunner. Configure FoundrySettings:Endpoint to enable it.");
            }

            // Same validation + persistence as CreateAsync, then drive the runner
            // synchronously so the caller (controller) can stream each event over
            // SSE. Final state is written back to the store before the enumerable
            // completes so /sessions/{id} reflects the result for replay.
            var agent = await _agentStore.TryGetAsync(request.AgentName, cancellationToken).ConfigureAwait(false)
                ?? throw new ArgumentException($"Agent '{request.AgentName}' does not exist.");

            if (!await _permissionProvider.CheckAccessAsync(accessContext, agent, Operation.Read).ConfigureAwait(false))
            {
                _logger.LogWarning("User {userId} denied read access on agent {agent} for streaming run.", accessContext.GetUserId(), agent.Name.Sanitize());
                throw new UnauthorizedAccessException("You do not have permission to invoke this agent.");
            }

            var session = SessionResource.Create(request, agent, accessContext.GetUserId()!, DateTimeOffset.UtcNow);
            session.Status = SessionStatus.Running;
            session.WorkingDirectory = AllocateWorkingDirectory(session.Id);
            _logger.LogInformation("Streaming run for /sessions/{id} on agent {agent}.", session.Id, agent.Name.Sanitize());
            await _store.UpsertAsync(session, cancellationToken).ConfigureAwait(false);

            var enumerator = _agentRunner.RunStreamingAsync(
                agent,
                session.Input,
                session.Id,
                parentSessionId: session.ParentSessionId,
                history: session.Messages,
                workingDirectory: session.WorkingDirectory,
                accessContext,
                cancellationToken).GetAsyncEnumerator(cancellationToken);
            try
            {
                while (true)
                {
                    SessionEvent evt;
                    try
                    {
                        if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                        {
                            break;
                        }
                        evt = enumerator.Current;
                    }
                    catch (OperationCanceledException)
                    {
                        // Client disconnected or cancellation token tripped before
                        // the runner could emit Completed/Failed. Persist a terminal
                        // status so the session record doesn't stay stuck on Running.
                        await PersistTerminalStatusAsync(session, SessionStatus.Cancelled, error: "Run cancelled before completion.").ConfigureAwait(false);
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Streaming run for session {id} faulted.", session.Id);
                        await PersistTerminalStatusAsync(session, SessionStatus.Failed, error: ex.Message).ConfigureAwait(false);
                        throw;
                    }

                    if (evt.Type == SessionEventType.Completed)
                    {
                        session.Status = SessionStatus.Completed;
                        session.Result = evt.Answer;
                        session.LastUpdatedAt = DateTimeOffset.UtcNow;
                        await _store.UpsertAsync(session, CancellationToken.None).ConfigureAwait(false);
                    }
                    else if (evt.Type == SessionEventType.Failed)
                    {
                        session.Status = SessionStatus.Failed;
                        session.Error = evt.Error;
                        session.LastUpdatedAt = DateTimeOffset.UtcNow;
                        await _store.UpsertAsync(session, CancellationToken.None).ConfigureAwait(false);
                    }
                    yield return evt;
                }

                // If the runner completed without ever emitting a terminal
                // event (defensive; AgentRunner contract says it always does),
                // mark the session Completed so it doesn't stay Running.
                if (session.Status == SessionStatus.Running)
                {
                    await PersistTerminalStatusAsync(session, SessionStatus.Completed, error: null).ConfigureAwait(false);
                }
            }
            finally
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }
        }

        public async IAsyncEnumerable<SessionEvent> ContinueStreamingAsync(
            ClaimsPrincipal accessContext,
            string sessionId,
            string userInput,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(accessContext);
            ArgumentException.ThrowIfNullOrEmpty(sessionId);
            ArgumentException.ThrowIfNullOrEmpty(userInput);
            if (_agentRunner == null)
            {
                throw new InvalidOperationException("Streaming runs require an AgentRunner. Configure FoundrySettings:Endpoint to enable it.");
            }

            var session = await _store.TryGetAsync(sessionId, cancellationToken).ConfigureAwait(false)
                ?? throw new ArgumentException($"Session '{sessionId}' does not exist.");
            await EnsureAccessAsync(accessContext, session, Operation.Write).ConfigureAwait(false);

            // Snapshot prior messages BEFORE appending the new user turn so the
            // runner replays only past history, not the live message it is about
            // to process.
            var priorHistory = session.Messages.ToList();
            session.Status = SessionStatus.Running;
            session.LastUpdatedAt = DateTimeOffset.UtcNow;
            await _store.UpsertAsync(session, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Continuing /sessions/{id} with new user message ({len} chars, {prior} prior messages).",
                session.Id, userInput.Length, priorHistory.Count);

            var enumerator = _agentRunner.RunStreamingAsync(
                session.AgentSnapshot,
                userInput,
                session.Id,
                parentSessionId: session.ParentSessionId,
                history: session.Messages,
                workingDirectory: session.WorkingDirectory,
                accessContext,
                cancellationToken,
                priorHistory: priorHistory).GetAsyncEnumerator(cancellationToken);
            try
            {
                while (true)
                {
                    SessionEvent evt;
                    try
                    {
                        if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                        {
                            break;
                        }
                        evt = enumerator.Current;
                    }
                    catch (OperationCanceledException)
                    {
                        await PersistTerminalStatusAsync(session, SessionStatus.Cancelled, error: "Run cancelled before completion.").ConfigureAwait(false);
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Continuing session {id} faulted.", session.Id);
                        await PersistTerminalStatusAsync(session, SessionStatus.Failed, error: ex.Message).ConfigureAwait(false);
                        throw;
                    }

                    if (evt.Type == SessionEventType.Completed)
                    {
                        session.Status = SessionStatus.Completed;
                        session.Result = evt.Answer;
                        session.LastUpdatedAt = DateTimeOffset.UtcNow;
                        await _store.UpsertAsync(session, CancellationToken.None).ConfigureAwait(false);
                    }
                    else if (evt.Type == SessionEventType.Failed)
                    {
                        session.Status = SessionStatus.Failed;
                        session.Error = evt.Error;
                        session.LastUpdatedAt = DateTimeOffset.UtcNow;
                        await _store.UpsertAsync(session, CancellationToken.None).ConfigureAwait(false);
                    }
                    yield return evt;
                }

                if (session.Status == SessionStatus.Running)
                {
                    await PersistTerminalStatusAsync(session, SessionStatus.Completed, error: null).ConfigureAwait(false);
                }
            }
            finally
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Best-effort persistence of a terminal session state. Swallows store
        /// errors so the original cancellation / exception flow isn't masked.
        /// </summary>
        private async Task PersistTerminalStatusAsync(SessionResource session, SessionStatus status, string? error)
        {
            try
            {
                session.Status = status;
                if (error != null)
                {
                    session.Error = error;
                }
                session.LastUpdatedAt = DateTimeOffset.UtcNow;
                await _store.UpsertAsync(session, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to persist terminal status {status} for session {id}.", status, session.Id);
            }
        }

        public async Task<SessionResource?> GetAsync(ClaimsPrincipal accessContext, string id, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(accessContext);
            ArgumentException.ThrowIfNullOrEmpty(id);

            _logger.LogInformation("Getting /sessions/{id}.", id.Sanitize());
            var session = await _store.TryGetAsync(id, cancellationToken).ConfigureAwait(false);
            if (session == null)
            {
                return null;
            }

            await EnsureAccessAsync(accessContext, session, Operation.Read).ConfigureAwait(false);
            return session;
        }

        public async Task DeleteAsync(ClaimsPrincipal accessContext, string id, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(accessContext);
            ArgumentException.ThrowIfNullOrEmpty(id);

            _logger.LogInformation("Deleting /sessions/{id}.", id.Sanitize());
            var existing = await _store.TryGetAsync(id, cancellationToken).ConfigureAwait(false)
                ?? throw new ArgumentException("The session does not exist.");

            await EnsureAccessAsync(accessContext, existing, Operation.Write).ConfigureAwait(false);
            await _store.DeleteAsync(id, cancellationToken).ConfigureAwait(false);

            // Release per-session quota bookkeeping and best-effort scrub the
            // session's working directory so long-lived pods don't accumulate
            // orphaned counters or temp files.
            if (!string.IsNullOrEmpty(existing.WorkingDirectory))
            {
                _builtinExecutor?.ReleaseSession(existing.WorkingDirectory);
                try
                {
                    if (Directory.Exists(existing.WorkingDirectory))
                    {
                        Directory.Delete(existing.WorkingDirectory, recursive: true);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete working directory for session {id}.", id.Sanitize());
                }
            }
        }

        public async Task<IEnumerable<SessionResource>> ListAsync(ClaimsPrincipal accessContext, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(accessContext);

            _logger.LogInformation("Listing /sessions for user.");
            var resources = (await _store.ListAsync(cancellationToken).ConfigureAwait(false)).ToList();
            var allowed = await _permissionProvider.CheckAccessAsync(accessContext, resources, Operation.Read).ConfigureAwait(false);

            var filteredCount = resources.Count - allowed.Length;
            if (filteredCount > 0)
            {
                _logger.LogInformation("Filtered {count} session resources due to authorization.", filteredCount);
            }

            return allowed;
        }

        private async Task EnsureAccessAsync(ClaimsPrincipal accessContext, SessionResource resource, Operation operation)
        {
            ArgumentNullException.ThrowIfNull(accessContext);
            ArgumentNullException.ThrowIfNull(resource);

            if (await _permissionProvider.CheckAccessAsync(accessContext, resource, operation).ConfigureAwait(false))
            {
                return;
            }

            var operationName = operation.ToString().ToLowerInvariant();
            _logger.LogWarning("User {userId} is denied {operation} access for session {resourceId}.", accessContext.GetUserId(), operationName, resource.Id);
            throw new UnauthorizedAccessException("You do not have permission to perform the operation.");
        }

        /// <summary>
        /// Allocate a per-session working directory for built-in tools. Located
        /// under the OS temp dir so the gateway pod can recycle on restart.
        /// P2 will replace this with a sandboxed/quota'd path.
        /// </summary>
        private static string AllocateWorkingDirectory(string sessionId)
        {
            var path = Path.Combine(Path.GetTempPath(), "agent-sessions", sessionId);
            Directory.CreateDirectory(path);
            return path;
        }
    }
}
