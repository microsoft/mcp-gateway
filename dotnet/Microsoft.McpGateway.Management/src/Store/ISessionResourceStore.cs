// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.McpGateway.Management.Contracts;

namespace Microsoft.McpGateway.Management.Store
{
    /// <summary>
    /// Interface for storing and retrieving session resources.
    /// </summary>
    public interface ISessionResourceStore
    {
        Task<SessionResource?> TryGetAsync(string id, CancellationToken cancellationToken);

        Task UpsertAsync(SessionResource session, CancellationToken cancellationToken);

        Task DeleteAsync(string id, CancellationToken cancellationToken);

        Task<IEnumerable<SessionResource>> ListAsync(CancellationToken cancellationToken);
    }
}
