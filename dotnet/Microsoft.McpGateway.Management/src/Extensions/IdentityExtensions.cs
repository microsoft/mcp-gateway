// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Security.Claims;

namespace Microsoft.McpGateway.Management.Extensions
{
    public static class IdentityExtensions
    {
        public static string? GetUserId(this ClaimsPrincipal principal) =>
            principal?.Claims?.FirstOrDefault(r => r.Type == ClaimTypes.NameIdentifier)?.Value ??
            principal?.Claims?.FirstOrDefault(r => r.Type == "oid")?.Value;

        public static IReadOnlyCollection<string> GetUserRoles(this ClaimsPrincipal principal)
        {
            if (principal?.Claims == null)
                return [];

            var roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var claimType in new[] { ClaimTypes.Role, "roles", "role" })
            {
                foreach (var claim in principal.FindAll(claimType))
                {
                    if (!string.IsNullOrWhiteSpace(claim.Value))
                    {
                        roles.Add(claim.Value.Trim());
                    }
                }
            }

            return roles;
        }

        public static bool IsInRole(this ClaimsPrincipal principal, string role) =>
            !string.IsNullOrWhiteSpace(role) &&
            principal.GetUserRoles().Any(r => string.Equals(r, role, StringComparison.OrdinalIgnoreCase));
    }
}
