// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Security.Claims;
using Microsoft.McpGateway.Management.Contracts;
using Microsoft.McpGateway.Management.Foundry;

namespace Microsoft.McpGateway.Management.Service
{
    /// <summary>
    /// Interface for managing agent sessions (one execution of an agent definition).
    /// </summary>
    public interface ISessionManagementService
    {
        /// <summary>
        /// Create a new session against an existing agent. Snapshots the agent definition.
        /// The agent loop runs in the background; clients poll <see cref="GetAsync"/> for state.
        /// </summary>
        Task<SessionResource> CreateAsync(ClaimsPrincipal accessContext, SessionData request, CancellationToken cancellationToken);

        /// <summary>
        /// Create a session and synchronously stream agent events as they happen.
        /// The session is persisted before the first event is yielded; the final
        /// <see cref="SessionEventType.Completed"/> / <see cref="SessionEventType.Failed"/>
        /// event is also persisted to the store before the enumerable completes.
        /// </summary>
        IAsyncEnumerable<SessionEvent> RunStreamingAsync(ClaimsPrincipal accessContext, SessionData request, CancellationToken cancellationToken);

        /// <summary>
        /// Continue an existing session by appending a new user message and
        /// streaming the agent's response. Replays prior User/Assistant
        /// messages as conversation context. The new turn's messages are
        /// appended to the persisted history.
        /// </summary>
        IAsyncEnumerable<SessionEvent> ContinueStreamingAsync(ClaimsPrincipal accessContext, string sessionId, string userInput, CancellationToken cancellationToken);

        /// <summary>
        /// Get a session by id.
        /// </summary>
        Task<SessionResource?> GetAsync(ClaimsPrincipal accessContext, string id, CancellationToken cancellationToken);

        /// <summary>
        /// Delete a session by id.
        /// </summary>
        Task DeleteAsync(ClaimsPrincipal accessContext, string id, CancellationToken cancellationToken);

        /// <summary>
        /// List all sessions accessible to the caller.
        /// </summary>
        Task<IEnumerable<SessionResource>> ListAsync(ClaimsPrincipal accessContext, CancellationToken cancellationToken);
    }
}
