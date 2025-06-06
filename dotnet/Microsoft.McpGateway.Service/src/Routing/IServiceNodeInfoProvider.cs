// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.McpGateway.Service.Routing
{
    /// <summary>
    /// Provides information about service node addresses where HTTP listeners are available.
    /// </summary>
    public interface IServiceNodeInfoProvider
    {
        /// <summary>
        /// Get all node addresses where the service HTTP listener listens on.
        /// </summary>
        /// <param name="serviceName">The service HTTP listener name.</param>
        /// <returns></returns>
        Task<IDictionary<string, string>> GetNodeAddressesAsync(string serviceName, CancellationToken cancellationToken);
    }
}
