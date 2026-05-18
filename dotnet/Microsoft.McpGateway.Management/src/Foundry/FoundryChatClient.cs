// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Azure.AI.OpenAI;
using Azure.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI.Chat;

namespace Microsoft.McpGateway.Management.Foundry
{
    /// <summary>
    /// <see cref="IFoundryChatClient"/> implementation using the Azure.AI.OpenAI
    /// SDK against an AIServices resource. Authentication uses the supplied
    /// <see cref="TokenCredential"/> (typically <c>DefaultAzureCredential</c> →
    /// workload identity in cluster).
    /// </summary>
    public class FoundryChatClient : IFoundryChatClient
    {
        private readonly AzureOpenAIClient _client;
        private readonly FoundrySettings _settings;
        private readonly ILogger<FoundryChatClient> _logger;

        public FoundryChatClient(
            IOptions<FoundrySettings> settings,
            TokenCredential credential,
            ILogger<FoundryChatClient> logger)
        {
            ArgumentNullException.ThrowIfNull(settings);
            ArgumentNullException.ThrowIfNull(credential);
            ArgumentNullException.ThrowIfNull(logger);

            _settings = settings.Value;
            if (string.IsNullOrWhiteSpace(_settings.Endpoint))
            {
                throw new InvalidOperationException("FoundrySettings:Endpoint is not configured.");
            }
            if (string.IsNullOrWhiteSpace(_settings.DeploymentName))
            {
                throw new InvalidOperationException("FoundrySettings:DeploymentName is not configured.");
            }

            _client = new AzureOpenAIClient(new Uri(_settings.Endpoint), credential);
            _logger = logger;
        }

        public async Task<ChatCompletion> CompleteAsync(
            IList<ChatMessage> messages,
            IEnumerable<ChatTool> tools,
            string? deploymentNameOverride,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(messages);

            var deployment = string.IsNullOrWhiteSpace(deploymentNameOverride)
                ? _settings.DeploymentName
                : deploymentNameOverride;

            var chat = _client.GetChatClient(deployment);
            var options = new ChatCompletionOptions();
            if (tools != null)
            {
                foreach (var tool in tools)
                {
                    options.Tools.Add(tool);
                }
            }

            _logger.LogInformation("Foundry chat completion: deployment={deployment} messages={count} tools={tools}", deployment, messages.Count, options.Tools.Count);

            var response = await chat.CompleteChatAsync(messages, options, cancellationToken).ConfigureAwait(false);
            return response.Value;
        }

        public async IAsyncEnumerable<StreamingChatCompletionUpdate> CompleteStreamingAsync(
            IList<ChatMessage> messages,
            IEnumerable<ChatTool> tools,
            string? deploymentNameOverride,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(messages);

            var deployment = string.IsNullOrWhiteSpace(deploymentNameOverride)
                ? _settings.DeploymentName
                : deploymentNameOverride;

            var chat = _client.GetChatClient(deployment);
            var options = new ChatCompletionOptions();
            if (tools != null)
            {
                foreach (var tool in tools)
                {
                    options.Tools.Add(tool);
                }
            }

            _logger.LogInformation("Foundry chat streaming: deployment={deployment} messages={count} tools={tools}", deployment, messages.Count, options.Tools.Count);

            await foreach (var update in chat.CompleteChatStreamingAsync(messages, options, cancellationToken).ConfigureAwait(false))
            {
                yield return update;
            }
        }
    }
}
