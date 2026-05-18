// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using OpenAI.Chat;

namespace Microsoft.McpGateway.Management.Foundry
{
    /// <summary>
    /// Thin wrapper around an Azure OpenAI chat deployment used by the agent
    /// runner. The runner owns message history + the tool loop; this client
    /// just executes a single completion call (with tools attached).
    /// </summary>
    public interface IFoundryChatClient
    {
        /// <summary>
        /// Send the supplied message history (including any prior assistant /
        /// tool messages) and return the next assistant message. The caller is
        /// responsible for handling tool calls and re-invoking.
        /// </summary>
        Task<ChatCompletion> CompleteAsync(
            IList<ChatMessage> messages,
            IEnumerable<ChatTool> tools,
            string? deploymentNameOverride,
            CancellationToken cancellationToken);

        /// <summary>
        /// Streaming variant: yield each <see cref="StreamingChatCompletionUpdate"/>
        /// from the model as it arrives. Used by the agent loop to surface
        /// token-level deltas to clients while still accumulating tool-call
        /// fragments for dispatch when the stream completes.
        /// </summary>
        IAsyncEnumerable<StreamingChatCompletionUpdate> CompleteStreamingAsync(
            IList<ChatMessage> messages,
            IEnumerable<ChatTool> tools,
            string? deploymentNameOverride,
            CancellationToken cancellationToken);
    }
}
