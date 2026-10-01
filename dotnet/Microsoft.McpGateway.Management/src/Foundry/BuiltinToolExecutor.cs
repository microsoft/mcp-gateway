// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Microsoft.McpGateway.Management.Foundry
{
    /// <summary>
    /// Built-in agent tools are disabled. Retained entry points reject
    /// execution, including calls from existing agents and sessions.
    /// </summary>
    public class BuiltinToolExecutor
    {
        public const string Bash = "builtin_bash";
        public const string ReadFile = "builtin_read_file";
        public const string WriteFile = "builtin_write_file";
        internal const string DisabledMessage = "Built-in tools are disabled.";

        public static readonly IReadOnlyList<string> SupportedKinds = Array.Empty<string>();

        private readonly ILogger<BuiltinToolExecutor> _logger;

        public BuiltinToolExecutor(ILogger<BuiltinToolExecutor> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public Task<ToolResult> ExecuteAsync(string kind, string argumentsJson, string workingDirectory, CancellationToken cancellationToken)
        {
            _logger.LogWarning("Built-in tool '{tool}' is disabled; rejecting execution.", kind);
            return Task.FromResult(Error(DisabledMessage));
        }

        public static global::OpenAI.Chat.ChatTool BuildChatTool(string kind)
        {
            throw new NotSupportedException(DisabledMessage);
        }

        /// <summary>
        /// Retained for session cleanup compatibility. Disabled tools have
        /// no per-session execution state to release.
        /// </summary>
        public void ReleaseSession(string workingDirectory)
        {
        }

        private static ToolResult Error(string message) =>
            new(JsonSerializer.Serialize(new { error = message }), IsError: true);
    }
}
