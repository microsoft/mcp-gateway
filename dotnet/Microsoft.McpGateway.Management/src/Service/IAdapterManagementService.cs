// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Security.Claims;
using Microsoft.McpGateway.Management.Contracts;

namespace Microsoft.McpGateway.Management.Service
{
    /// <summary>
    /// Defines operations for managing MCP adapter servers, including creation,
    /// update, retrieval, deletion, and listing, within the identity of the access context.
    /// </summary>
    public interface IAdapterManagementService
    {
        /// <summary>
        /// Creates a new adapter server and registers it within the system.
        /// </summary>
        /// <param name="accessContext">Access context representing the user.</param>
        /// <param name="request">Adapter configuration data provided by the client.</param>
        /// <param name="cancellationToken">Token to cancel the operation.</param>
        /// <returns>The newly created adapter with metadata.</returns>
        Task<AdapterResource> CreateAsync(ClaimsPrincipal accessContext, AdapterData request, CancellationToken cancellationToken);

        /// <summary>
        /// Retrieves metadata for a specific adapter by name.
        /// </summary>
        /// <param name="accessContext">Access context representing the user.</param>
        /// <param name="name">The name of the adapter.</param>
        /// <param name="cancellationToken">Token to cancel the operation.</param>
        /// <returns>The adapter metadata if found.</returns>
        Task<AdapterResource?> GetAsync(ClaimsPrincipal accessContext, string name, CancellationToken cancellationToken);

        /// <summary>
        /// Updates the configuration of an existing MCP adapter server.
        /// </summary>
        /// <param name="accessContext">Access context representing the user.</param>
        /// <param name="request">Updated adapter configuration.</param>
        /// <param name="cancellationToken">Token to cancel the operation.</param>
        /// <returns>The updated adapter metadata.</returns>
        Task<AdapterResource> UpdateAsync(ClaimsPrincipal accessContext, AdapterData request, CancellationToken cancellationToken);

        /// <summary>
        /// Deletes a specific adapter by name from the system.
        /// </summary>
        /// <param name="accessContext">Access context representing the user.</param>
        /// <param name="name">The name of the adapter to delete.</param>
        /// <param name="cancellationToken">Token to cancel the operation.</param>
        Task DeleteAsync(ClaimsPrincipal accessContext, string name, CancellationToken cancellationToken);

        /// <summary>
        /// Lists all adapters accessible within the given access context.
        /// </summary>
        /// <param name="accessContext">Access context representing the user.</param>
        /// <param name="cancellationToken">Token to cancel the operation.</param>
        /// <returns>A collection of adapter metadata entries that user can access.</returns>
        Task<IEnumerable<AdapterResource>> ListAsync(ClaimsPrincipal accessContext, CancellationToken cancellationToken);
    }
}
