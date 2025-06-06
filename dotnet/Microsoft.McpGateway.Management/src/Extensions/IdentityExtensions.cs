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
    }
}
