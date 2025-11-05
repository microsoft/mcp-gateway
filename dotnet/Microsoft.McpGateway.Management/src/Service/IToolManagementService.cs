// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Security.Claims;
using Microsoft.McpGateway.Management.Contracts;

namespace Microsoft.McpGateway.Management.Service
{
    /// <summary>
    /// Interface for managing tool deployments.
    /// </summary>
    public interface IToolManagementService
    {
        /// <summary>
        /// Create a new tool deployment.
        /// </summary>
        Task<ToolResource> CreateAsync(ClaimsPrincipal accessContext, ToolData request, CancellationToken cancellationToken);

        /// <summary>
        /// Get a tool deployment by name.
        /// </summary>
        Task<ToolResource?> GetAsync(ClaimsPrincipal accessContext, string name, CancellationToken cancellationToken);

        /// <summary>
        /// Update an existing tool deployment.
        /// </summary>
        Task<ToolResource> UpdateAsync(ClaimsPrincipal accessContext, ToolData request, CancellationToken cancellationToken);

        /// <summary>
        /// Delete a tool deployment.
        /// </summary>
        Task DeleteAsync(ClaimsPrincipal accessContext, string name, CancellationToken cancellationToken);

        /// <summary>
        /// List all tool deployments.
        /// </summary>
        Task<IEnumerable<ToolResource>> ListAsync(ClaimsPrincipal accessContext, CancellationToken cancellationToken);
    }
}
