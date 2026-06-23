// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Security.Claims;
using Microsoft.Extensions.Options;
using Microsoft.McpGateway.Management.Extensions;

namespace Microsoft.McpGateway.Management.Authorization
{
    /// <summary>
    /// Default <see cref="IBuiltinToolAuthorizer"/>: a caller may use built-in
    /// tools when they hold <c>mcp.admin</c> or any role configured in
    /// <see cref="BuiltinToolSettings.RequiredRoles"/>. Fail-closed — with no
    /// configuration only administrators are permitted.
    /// </summary>
    public sealed class BuiltinToolAuthorizer : IBuiltinToolAuthorizer
    {
        private const string AdminRole = "mcp.admin";

        private readonly HashSet<string> _allowedRoles;

        public BuiltinToolAuthorizer(IOptions<BuiltinToolSettings> options)
        {
            ArgumentNullException.ThrowIfNull(options);

            _allowedRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { AdminRole };
            foreach (var role in options.Value?.RequiredRoles ?? Enumerable.Empty<string>())
            {
                if (!string.IsNullOrWhiteSpace(role))
                {
                    _allowedRoles.Add(role.Trim());
                }
            }
        }

        public bool IsAuthorized(ClaimsPrincipal principal)
        {
            ArgumentNullException.ThrowIfNull(principal);
            return principal.GetUserRoles().Any(role => _allowedRoles.Contains(role));
        }
    }
}
