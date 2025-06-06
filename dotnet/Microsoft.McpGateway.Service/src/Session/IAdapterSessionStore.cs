// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.McpGateway.Service.Session
{
    /// <summary>
    /// The session store for managing session connection for the reverse proxy.
    /// </summary>
    public interface IAdapterSessionStore
    {
        /// <summary>
        /// Gets the target URL mapped to the specified session ID.
        /// </summary>
        /// <param name="sessionId">The session ID.</param>
        /// <returns>
        /// A tuple containing the target (if found) and a flag indicating whether it exists.
        /// </returns>
        Task<(string? target, bool exists)> TryGetAsync(string sessionId, CancellationToken cancellationToken);

        /// <summary>
        /// Sets or updates the target URL for the given session ID.
        /// </summary>
        /// <param name="sessionId">The session ID to store.</param>
        /// <param name="target">The target URL to associate.</param>
        Task SetAsync(string sessionId, string target, CancellationToken cancellationToken);

        /// <summary>
        /// Removes the session mapping for the given session ID.
        /// </summary>
        /// <param name="sessionId">The session ID to remove.</param>
        Task RemoveAsync(string sessionId, CancellationToken cancellationToken);
    }
}
