// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.McpGateway.Service.Session
{
    /// <summary>
    /// Interface for resolving target addresses based on session routing logic.
    /// </summary>
    public interface ISessionRoutingHandler
    {
        /// <summary>
        /// Get the target address for an existing session asynchronously.
        /// </summary>
        /// <param name="adapterName">The name of the adapter named on the request route. The session is resolved only when it was originally created against this same adapter.</param>
        /// <param name="httpContext">The incoming HTTP context</param>
        /// <param name="cancellationToken">Token to monitor for cancellation requests</param>
        /// <returns>A task that represents the asynchronous operation, containing the target address as a string, null if no target found.</returns>
        Task<string?> GetExistingSessionTargetAsync(string adapterName, HttpContext httpContext, CancellationToken cancellationToken);

        /// <summary>
        /// Get the target address for a new session asynchronously.
        /// </summary>
        /// <param name="adapterName">The name of the adapter to be used for the new session</param>
        /// <param name="httpContext">The incoming HTTP context</param>
        /// <param name="cancellationToken">Token to monitor for cancellation requests</param>
        /// <returns>A task that represents the asynchronous operation, containing the target address as a string</returns>
        Task<string?> GetNewSessionTargetAsync(string adapterName, HttpContext httpContext, CancellationToken cancellationToken);
    }
}
