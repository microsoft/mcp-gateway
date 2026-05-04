// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using Microsoft.McpGateway.Management.Contracts;

namespace Microsoft.McpGateway.Management.Store
{
    /// <summary>
    /// In-memory implementation of the session resource store for development.
    /// </summary>
    public class InMemorySessionResourceStore : ISessionResourceStore
    {
        private readonly ConcurrentDictionary<string, SessionResource> _sessions = new();

        public Task<SessionResource?> TryGetAsync(string id, CancellationToken cancellationToken)
        {
            _sessions.TryGetValue(id, out var session);
            return Task.FromResult(session);
        }

        public Task UpsertAsync(SessionResource session, CancellationToken cancellationToken)
        {
            _sessions[session.Id] = session;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string id, CancellationToken cancellationToken)
        {
            _sessions.TryRemove(id, out _);
            return Task.CompletedTask;
        }

        public Task<IEnumerable<SessionResource>> ListAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<IEnumerable<SessionResource>>([.. _sessions.Values]);
        }
    }
}
