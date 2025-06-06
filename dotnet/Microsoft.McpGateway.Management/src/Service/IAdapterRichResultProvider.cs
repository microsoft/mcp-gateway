// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.McpGateway.Management.Contracts;

namespace Microsoft.McpGateway.Management.Service
{
    public interface IAdapterRichResultProvider
    {
        /// <summary>
        /// Retrieves the current deployment status of the specified adapter.
        /// </summary>
        /// <param name="name">The name of the adapter.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains the adapter's deployment status.</returns>
        Task<AdapterStatus> GetAdapterStatusAsync(string name, CancellationToken cancellationToken);

        /// <summary>
        /// Retrieves the logs from a specific instance of a deployed adapter.
        /// </summary>
        /// <param name="name">The name of the adapter.</param>
        /// <param name="instance">The instance ID of the adapter. Defaults to 0 if not specified.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains the adapter's log information.</returns>
        Task<string> GetAdapterLogsAsync(string name, int instance = 0, CancellationToken cancellationToken = default);
    }
}
