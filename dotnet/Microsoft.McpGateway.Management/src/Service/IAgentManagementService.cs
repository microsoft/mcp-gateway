// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Security.Claims;
using Microsoft.McpGateway.Management.Contracts;

namespace Microsoft.McpGateway.Management.Service
{
    /// <summary>
    /// Interface for managing agent definitions. Agents are metadata-only;
    /// no Kubernetes deployment is involved.
    /// </summary>
    public interface IAgentManagementService
    {
        /// <summary>
        /// Create a new agent definition.
        /// </summary>
        Task<AgentResource> CreateAsync(ClaimsPrincipal accessContext, AgentData request, CancellationToken cancellationToken);

        /// <summary>
        /// Get an agent definition by name.
        /// </summary>
        Task<AgentResource?> GetAsync(ClaimsPrincipal accessContext, string name, CancellationToken cancellationToken);

        /// <summary>
        /// Update an existing agent definition.
        /// </summary>
        Task<AgentResource> UpdateAsync(ClaimsPrincipal accessContext, AgentData request, CancellationToken cancellationToken);

        /// <summary>
        /// Delete an agent definition.
        /// </summary>
        Task DeleteAsync(ClaimsPrincipal accessContext, string name, CancellationToken cancellationToken);

        /// <summary>
        /// List all agent definitions accessible to the caller.
        /// </summary>
        Task<IEnumerable<AgentResource>> ListAsync(ClaimsPrincipal accessContext, CancellationToken cancellationToken);
    }
}
