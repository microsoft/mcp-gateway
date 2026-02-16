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

        /// <summary>
        /// Register a tool definition from an adapter without creating a deployment.
        /// Tools are stored as metadata referencing their source adapter.
        /// </summary>
        /// <param name="adapterName">The name of the adapter that provides this tool.</param>
        /// <param name="tool">The MCP tool definition.</param>
        /// <param name="createdBy">The user ID of the creator.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        Task RegisterToolDefinitionAsync(string adapterName, ModelContextProtocol.Protocol.Tool tool, string createdBy, CancellationToken cancellationToken);

        /// <summary>
        /// Delete all tools associated with an adapter.
        /// </summary>
        /// <param name="adapterName">The name of the adapter whose tools should be deleted.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        Task DeleteToolsByAdapterAsync(string adapterName, CancellationToken cancellationToken);
    }
}
