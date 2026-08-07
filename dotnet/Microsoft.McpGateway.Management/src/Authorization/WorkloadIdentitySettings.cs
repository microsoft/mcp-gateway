// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.McpGateway.Management.Authorization
{
    /// <summary>
    /// Authorization configuration for binding deployed workloads to the
    /// cluster's shared workload identity. Bound from the
    /// "WorkloadIdentitySettings" section.
    /// </summary>
    public class WorkloadIdentitySettings
    {
        /// <summary>
        /// Roles permitted to request <c>useWorkloadIdentity</c> when creating
        /// or updating an adapter/tool, in addition to <c>mcp.admin</c> (which
        /// is always allowed). When empty (the default), workload identity is
        /// restricted to administrators only.
        /// </summary>
        public IList<string> RequiredRoles { get; set; } = [];
    }
}
