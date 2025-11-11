// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Security.Claims;
using Microsoft.McpGateway.Management.Contracts;
using Microsoft.McpGateway.Management.Extensions;

namespace Microsoft.McpGateway.Management.Authorization
{
    /// <summary>
    /// Simple permission evaluator that allows read access for creators, administrators, or members of a resource's allowed roles,
    /// and restricts write access to the resource owner or administrators.
    /// </summary>
    public class SimplePermissionProvider : IPermissionProvider
    {
        private const string AdminRole = "mcp.admin";

        public Task<bool> CheckAccessAsync(ClaimsPrincipal principal, IManagedResource resource, Operation operation)
        {
            ArgumentNullException.ThrowIfNull(principal);
            ArgumentNullException.ThrowIfNull(resource);

            var allowed = operation switch
            {
                Operation.Read => CanRead(principal, resource),
                Operation.Write => CanWrite(principal, resource),
                _ => false
            };

            return Task.FromResult(allowed);
        }

        public Task<T[]> CheckAccessAsync<T>(ClaimsPrincipal principal, IEnumerable<T> resources, Operation operation)
            where T : IManagedResource
        {
            ArgumentNullException.ThrowIfNull(principal);
            ArgumentNullException.ThrowIfNull(resources);

            var allowedResources = resources
                .Where(resource => resource is not null)
                .Where(resource => operation switch
                {
                    Operation.Read => CanRead(principal, resource!),
                    Operation.Write => CanWrite(principal, resource!),
                    _ => false
                })
                .ToArray();

            return Task.FromResult(allowedResources);
        }

        private static bool CanRead(ClaimsPrincipal principal, IManagedResource resource)
        {
            if (principal == null)
            {
                return false;
            }

            if (string.Equals(principal.GetUserId(), resource.CreatedBy, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var roles = principal.GetUserRoles();
            if (IsAdmin(roles))
            {
                return true;
            }

            if (resource.RequiredRoles == null || resource.RequiredRoles.Count == 0)
            {
                return true;
            }

            return resource.RequiredRoles.Any(role => roles.Any(userRole => string.Equals(userRole, role, StringComparison.OrdinalIgnoreCase)));
        }

        private static bool CanWrite(ClaimsPrincipal principal, IManagedResource resource)
        {
            if (principal == null)
            {
                return false;
            }

            if (string.Equals(principal.GetUserId(), resource.CreatedBy, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return IsAdmin(principal.GetUserRoles());
        }

        private static bool IsAdmin(IEnumerable<string> roles) =>
            roles.Any(role => string.Equals(role, AdminRole, StringComparison.OrdinalIgnoreCase));
    }
}
