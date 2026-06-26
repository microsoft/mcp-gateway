// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Security.Claims;

namespace Microsoft.McpGateway.Management.Authorization
{
    /// <summary>
    /// Authorizes use of the privileged in-process built-in tools
    /// (<c>bash</c>, <c>read_file</c>, <c>write_file</c>). Unlike
    /// <see cref="IPermissionProvider"/>, built-ins are not store-backed
    /// resources, so authorization is a capability check against the caller's
    /// roles rather than a per-resource ACL — notably there is no creator
    /// bypass, so even the author of an agent must hold the required role.
    /// </summary>
    public interface IBuiltinToolAuthorizer
    {
        /// <summary>
        /// Returns <see langword="true"/> when <paramref name="principal"/> is
        /// permitted to reference and invoke built-in tools.
        /// </summary>
        bool IsAuthorized(ClaimsPrincipal principal);
    }
}
