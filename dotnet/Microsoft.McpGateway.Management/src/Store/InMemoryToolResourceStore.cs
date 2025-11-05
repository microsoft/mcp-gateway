// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using Microsoft.McpGateway.Management.Contracts;

namespace Microsoft.McpGateway.Management.Store
{
    /// <summary>
    /// In-memory implementation of the tool resource store for development.
    /// </summary>
    public class InMemoryToolResourceStore : IToolResourceStore
    {
        private readonly ConcurrentDictionary<string, ToolResource> _tools = new();

        public Task<ToolResource?> TryGetAsync(string name, CancellationToken cancellationToken)
        {
            _tools.TryGetValue(name, out var tool);
            return Task.FromResult(tool);
        }

        public Task UpsertAsync(ToolResource tool, CancellationToken cancellationToken)
        {
            _tools[tool.Name] = tool;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string name, CancellationToken cancellationToken)
        {
            _tools.TryRemove(name, out _);
            return Task.CompletedTask;
        }

        public Task<IEnumerable<ToolResource>> ListAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<IEnumerable<ToolResource>>([.. _tools.Values]);
        }
    }
}
