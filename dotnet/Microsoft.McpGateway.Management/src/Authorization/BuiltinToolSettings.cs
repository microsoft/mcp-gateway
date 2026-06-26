// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.McpGateway.Management.Authorization
{
    /// <summary>
    /// Authorization configuration for the in-process built-in agent tools
    /// (<c>bash</c>, <c>read_file</c>, <c>write_file</c>). Bound from the
    /// "BuiltinToolSettings" section.
    /// </summary>
    public class BuiltinToolSettings
    {
        /// <summary>
        /// Roles permitted to reference and invoke built-in tools, in addition
        /// to <c>mcp.admin</c> (which is always allowed). When empty (the
        /// default), built-in tools are restricted to administrators only.
        /// </summary>
        public IList<string> RequiredRoles { get; set; } = [];
    }
}
