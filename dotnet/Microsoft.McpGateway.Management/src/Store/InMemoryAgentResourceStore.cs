// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using Microsoft.McpGateway.Management.Contracts;

namespace Microsoft.McpGateway.Management.Store
{
    /// <summary>
    /// In-memory implementation of the agent resource store for development.
    /// </summary>
    public class InMemoryAgentResourceStore : IAgentResourceStore
    {
        private readonly ConcurrentDictionary<string, AgentResource> _agents = new();

        public Task<AgentResource?> TryGetAsync(string name, CancellationToken cancellationToken)
        {
            _agents.TryGetValue(name, out var agent);
            return Task.FromResult(agent);
        }

        public Task UpsertAsync(AgentResource agent, CancellationToken cancellationToken)
        {
            _agents[agent.Name] = agent;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string name, CancellationToken cancellationToken)
        {
            _agents.TryRemove(name, out _);
            return Task.CompletedTask;
        }

        public Task<IEnumerable<AgentResource>> ListAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<IEnumerable<AgentResource>>([.. _agents.Values]);
        }
    }
}
