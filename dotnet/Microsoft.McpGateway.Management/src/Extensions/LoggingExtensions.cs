// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.McpGateway.Management.Extensions
{
    public static class LoggingExtensions
    {
        public static string? Sanitize(this object? logEntity) => logEntity?.ToString()?.Replace("\n", string.Empty).Replace("\t", string.Empty).Replace("\r", string.Empty);
    }
}
