// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.McpGateway.Management.Contracts;

namespace Microsoft.McpGateway.Management.Store
{
    /// <summary>
    /// The contract for storing and retrieving adapter resources.
    /// </summary>
    public interface IAdapterResourceStore
    {
        /// <summary>
        /// Attempts to retrieve an adapter resource by name.
        /// </summary>
        /// <param name="name">The name of the adapter resource.</param>
        /// <param name="cancellationToken">Token to cancel the retrieval operation.</param>
        /// <returns>The adapter resource if found; otherwise, null.</returns>
        Task<AdapterResource?> TryGetAsync(string name, CancellationToken cancellationToken);

        /// <summary>
        /// Creates or updates the specified adapter resource.
        /// </summary>
        /// <param name="adapter">The adapter resource to upsert.</param>
        /// <param name="cancellationToken">Token to cancel the operation.</param>
        Task UpsertAsync(AdapterResource adapter, CancellationToken cancellationToken);

        /// <summary>
        /// Deletes the adapter resource with the specified name.
        /// </summary>
        /// <param name="name">The name of the adapter resource to delete.</param>
        /// <param name="cancellationToken">Token to cancel the deletion operation.</param>
        Task DeleteAsync(string name, CancellationToken cancellationToken);

        /// <summary>
        /// Lists all adapter resources in the store.
        /// </summary>
        /// <param name="cancellationToken">Token to cancel the listing operation.</param>
        /// <returns>A collection of all adapter resources.</returns>
        Task<IEnumerable<AdapterResource>> ListAsync(CancellationToken cancellationToken);
    }
}
