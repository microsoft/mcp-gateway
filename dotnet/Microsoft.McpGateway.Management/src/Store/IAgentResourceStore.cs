// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.McpGateway.Management.Contracts;

namespace Microsoft.McpGateway.Management.Store
{
    /// <summary>
    /// Interface for storing and retrieving agent resources.
    /// </summary>
    public interface IAgentResourceStore
    {
        /// <summary>
        /// Try to get an agent resource by name.
        /// </summary>
        Task<AgentResource?> TryGetAsync(string name, CancellationToken cancellationToken);

        /// <summary>
        /// Upsert an agent resource.
        /// </summary>
        Task UpsertAsync(AgentResource agent, CancellationToken cancellationToken);

        /// <summary>
        /// Delete an agent resource by name.
        /// </summary>
        Task DeleteAsync(string name, CancellationToken cancellationToken);

        /// <summary>
        /// List all agent resources.
        /// </summary>
        Task<IEnumerable<AgentResource>> ListAsync(CancellationToken cancellationToken);
    }
}
