// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using Microsoft.McpGateway.Management.Contracts;

namespace Microsoft.McpGateway.Management.Store
{
    public class InMemoryAdapterResourceStore : IAdapterResourceStore
    {
        private readonly ConcurrentDictionary<string, AdapterResource> _store = new();

        public Task<AdapterResource?> TryGetAsync(string name, CancellationToken cancellationToken)
        {
            _store.TryGetValue(name, out var resource);
            return Task.FromResult(resource);
        }

        public Task UpsertAsync(AdapterResource adapter, CancellationToken cancellationToken)
        {
            _store[adapter.Name] = adapter;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string name, CancellationToken cancellationToken)
        {
            _store.TryRemove(name, out _);
            return Task.CompletedTask;
        }

        public Task<IEnumerable<AdapterResource>> ListAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<IEnumerable<AdapterResource>>([.. _store.Values]);
        }
    }
}
