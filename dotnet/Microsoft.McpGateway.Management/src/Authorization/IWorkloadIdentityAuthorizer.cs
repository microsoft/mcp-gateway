// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Security.Claims;

namespace Microsoft.McpGateway.Management.Authorization
{
    /// <summary>
    /// Authorizes binding a deployed adapter/tool workload to the cluster's
    /// shared workload identity. Like <see cref="IBuiltinToolAuthorizer"/> —
    /// and unlike <see cref="IPermissionProvider"/> — this is a capability
    /// check against the caller's roles rather than a per-resource ACL, so
    /// there is no creator bypass: the federated identity is shared by every
    /// workload in the namespace and is not owned by the requester.
    /// </summary>
    public interface IWorkloadIdentityAuthorizer
    {
        /// <summary>
        /// Returns <see langword="true"/> when <paramref name="principal"/> is
        /// permitted to request <c>useWorkloadIdentity</c> on a deployment.
        /// </summary>
        bool IsAuthorized(ClaimsPrincipal principal);
    }
}
