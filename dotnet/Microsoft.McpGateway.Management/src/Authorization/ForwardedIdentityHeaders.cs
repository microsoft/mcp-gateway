// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.McpGateway.Management.Authorization
{
    /// <summary>
    /// Well-known header names used to forward authenticated principal context between gateway services.
    /// </summary>
    public static class ForwardedIdentityHeaders
    {
        public const string UserId = "X-Mcp-UserId";
        public const string UserName = "X-Mcp-UserName";
        public const string Roles = "X-Mcp-Roles";
    }
}
