// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.McpGateway.Management.Contracts;

namespace Microsoft.McpGateway.Management.Store
{
    /// <summary>
    /// Interface for storing and retrieving tool resources.
    /// </summary>
    public interface IToolResourceStore
    {
        /// <summary>
        /// Try to get a tool resource by name.
        /// </summary>
        Task<ToolResource?> TryGetAsync(string name, CancellationToken cancellationToken);

        /// <summary>
        /// Upsert a tool resource.
        /// </summary>
        Task UpsertAsync(ToolResource tool, CancellationToken cancellationToken);

        /// <summary>
        /// Delete a tool resource by name.
        /// </summary>
        Task DeleteAsync(string name, CancellationToken cancellationToken);

        /// <summary>
        /// List all tool resources.
        /// </summary>
        Task<IEnumerable<ToolResource>> ListAsync(CancellationToken cancellationToken);
    }
}
