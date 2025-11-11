// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Security.Claims;
using Microsoft.McpGateway.Management.Contracts;

namespace Microsoft.McpGateway.Management.Authorization
{
    public interface IPermissionProvider
    {
        Task<bool> CheckAccessAsync(ClaimsPrincipal principal, IManagedResource resource, Operation operation);

        Task<T[]> CheckAccessAsync<T>(ClaimsPrincipal principal, IEnumerable<T> resources, Operation operation)
            where T : IManagedResource;
    }
}
