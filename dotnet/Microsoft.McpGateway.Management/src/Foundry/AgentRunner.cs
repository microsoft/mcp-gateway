// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.McpGateway.Management.Contracts;
using OpenAI.Chat;

namespace Microsoft.McpGateway.Management.Foundry
{
    /// <summary>
    /// Runs an agent loop: send messages → if assistant requests tool calls,
    /// execute them and append results, then loop. Stops when the assistant
    /// returns a plain text response or when <see cref="MaxIterations"/> is hit.
    /// </summary>
    public class AgentRunner
    {
        public const int MaxIterations = 10;

        private readonly IFoundryChatClient _chat;
        private readonly AgentToolRegistry _toolRegistry;
        private readonly ILogger<AgentRunner> _logger;

        public AgentRunner(
            IFoundryChatClient chat,
            AgentToolRegistry toolRegistry,
            ILogger<AgentRunner> logger)
        {
            _chat = chat ?? throw new ArgumentNullException(nameof(chat));
            _toolRegistry = toolRegistry ?? throw new ArgumentNullException(nameof(toolRegistry));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Run the agent loop and return only the assistant's final text response.
        /// Used by callers that don't care about intermediate events.
        /// </summary>
        /// <param name="accessContext">
        /// The effective caller. Threaded down so nested tools/subagents are
        /// authorized against the caller's current roles (MSRC-122743).
        /// </param>
        public async Task<string> RunAsync(AgentResource agent, string userInput, ClaimsPrincipal accessContext, CancellationToken cancellationToken)
        {
            string answer = string.Empty;
            await foreach (var evt in RunStreamingAsync(agent, userInput, sessionId: string.Empty, parentSessionId: null, history: null, workingDirectory: null, accessContext, cancellationToken).ConfigureAwait(false))
            {
                if (evt.Type == SessionEventType.Completed)
                {
                    answer = evt.Answer ?? string.Empty;
                }
                else if (evt.Type == SessionEventType.Failed)
                {
                    throw new InvalidOperationException(evt.Error ?? "Agent run failed.");
                }
            }
            return answer;
        }

        /// <summary>
        /// Run the agent loop and yield each step as a <see cref="SessionEvent"/>.
        /// The sequence is always: Started → (ToolCallStarted, ToolCallCompleted)* → (Completed | Failed).
        /// </summary>
        /// <param name="history">
        /// Optional caller-owned message log. When supplied, the runner appends
        /// the user input, intermediate assistant/tool messages, and the final
        /// assistant reply so the caller can persist them for multi-turn
        /// continuation. When null, no history is recorded.
        /// </param>
        /// <param name="priorHistory">
        /// Optional prior history to seed the conversation when continuing an
        /// existing session. Only User and Assistant messages are replayed
        /// verbatim; Tool messages are dropped because OpenAI requires each
        /// Tool message be paired with the originating Assistant tool_call,
        /// which is not part of our provider-agnostic <see cref="SessionMessage"/>.
        /// </param>
        public async IAsyncEnumerable<SessionEvent> RunStreamingAsync(
            AgentResource agent,
            string userInput,
            string sessionId,
            string? parentSessionId,
            IList<SessionMessage>? history,
            string? workingDirectory,
            ClaimsPrincipal accessContext,
            [EnumeratorCancellation] CancellationToken cancellationToken,
            IReadOnlyList<SessionMessage>? priorHistory = null)
        {
            ArgumentNullException.ThrowIfNull(agent);
            ArgumentNullException.ThrowIfNull(accessContext);

            yield return new SessionEvent { Type = SessionEventType.Started, SessionId = sessionId, ParentSessionId = parentSessionId };

            IReadOnlyList<ResolvedTool> resolvedTools;
            List<ChatTool> chatTools;
            List<ChatMessage> messages;
            string? prepError = null;
            try
            {
                resolvedTools = await _toolRegistry.ResolveAsync(agent.Tools, accessContext, cancellationToken).ConfigureAwait(false);
                chatTools = AgentToolRegistry.BuildChatTools(resolvedTools);
                messages = new List<ChatMessage> { new SystemChatMessage(agent.System) };
                if (priorHistory != null)
                {
                    foreach (var prev in priorHistory)
                    {
                        switch (prev.Role)
                        {
                            case SessionMessageRole.User:
                                messages.Add(new UserChatMessage(prev.Content));
                                break;
                            case SessionMessageRole.Assistant:
                                if (!string.IsNullOrEmpty(prev.Content))
                                {
                                    messages.Add(new AssistantChatMessage(prev.Content));
                                }
                                break;
                            // Tool / System messages are intentionally skipped on replay.
                        }
                    }
                }
                messages.Add(new UserChatMessage(userInput));
                history?.Add(new SessionMessage { Role = SessionMessageRole.User, Content = userInput });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to prepare agent run for session {sid}.", sessionId);
                prepError = ex.Message;
                resolvedTools = Array.Empty<ResolvedTool>();
                chatTools = new List<ChatTool>();
                messages = new List<ChatMessage>();
            }

            if (prepError != null)
            {
                yield return new SessionEvent { Type = SessionEventType.Failed, SessionId = sessionId, ParentSessionId = parentSessionId, Error = prepError };
                yield break;
            }

            for (var iteration = 0; iteration < MaxIterations; iteration++)
            {
                // Streaming accumulation: collect content text + reassemble
                // tool-call fragments. The OpenAI streaming API yields content
                // tokens and tool-call argument fragments interleaved; we
                // surface content as TokenDelta events live and dispatch any
                // tool calls only after the stream finishes.
                var contentBuffer = new System.Text.StringBuilder();
                var toolCallAcc = new SortedDictionary<int, AccumulatedToolCall>();
                OpenAI.Chat.ChatFinishReason? finishReason = null;
                string? completionError = null;

                IAsyncEnumerator<StreamingChatCompletionUpdate>? enumerator = null;
                try
                {
                    enumerator = _chat.CompleteStreamingAsync(messages, chatTools, agent.Model, cancellationToken).GetAsyncEnumerator(cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to start chat stream at iteration {iter} for session {sid}.", iteration, sessionId);
                    completionError = ex.Message;
                }

                if (enumerator != null)
                {
                    try
                    {
                        while (true)
                        {
                            bool hasNext;
                            StreamingChatCompletionUpdate? update = null;
                            try
                            {
                                hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
                                if (hasNext) update = enumerator.Current;
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Chat stream error at iteration {iter} for session {sid}.", iteration, sessionId);
                                completionError = ex.Message;
                                break;
                            }
                            if (!hasNext || update == null) break;

                            // Collect deltas from this update outside try so the yield works
                            // below; defensively guard accessor exceptions on each property.
                            string? deltaThisUpdate = null;
                            try
                            {
                                if (update.ContentUpdate != null && update.ContentUpdate.Count > 0)
                                {
                                    var sb = new System.Text.StringBuilder();
                                    foreach (var part in update.ContentUpdate)
                                    {
                                        var t = part?.Text;
                                        if (!string.IsNullOrEmpty(t)) sb.Append(t);
                                    }
                                    if (sb.Length > 0)
                                    {
                                        deltaThisUpdate = sb.ToString();
                                        contentBuffer.Append(deltaThisUpdate);
                                    }
                                }
                                if (update.ToolCallUpdates != null && update.ToolCallUpdates.Count > 0)
                                {
                                    foreach (var tu in update.ToolCallUpdates)
                                    {
                                        if (!toolCallAcc.TryGetValue(tu.Index, out var acc))
                                        {
                                            acc = new AccumulatedToolCall();
                                            toolCallAcc[tu.Index] = acc;
                                        }
                                        if (!string.IsNullOrEmpty(tu.ToolCallId)) acc.Id = tu.ToolCallId;
                                        if (!string.IsNullOrEmpty(tu.FunctionName)) acc.Name = tu.FunctionName;
                                        if (tu.FunctionArgumentsUpdate != null && tu.FunctionArgumentsUpdate.ToMemory().Length > 0)
                                        {
                                            var frag = tu.FunctionArgumentsUpdate.ToString();
                                            if (!string.IsNullOrEmpty(frag)) acc.Arguments.Append(frag);
                                        }
                                    }
                                }
                                if (update.FinishReason.HasValue) finishReason = update.FinishReason;
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Failed processing stream update at iteration {iter} for session {sid}.", iteration, sessionId);
                                completionError = ex.Message;
                                break;
                            }

                            if (deltaThisUpdate != null)
                            {
                                yield return new SessionEvent
                                {
                                    Type = SessionEventType.TokenDelta,
                                    SessionId = sessionId,
                                    ParentSessionId = parentSessionId,
                                    Iteration = iteration,
                                    DeltaText = deltaThisUpdate,
                                };
                            }
                        }
                    }
                    finally
                    {
                        await enumerator.DisposeAsync().ConfigureAwait(false);
                    }
                }

                if (completionError != null)
                {
                    yield return new SessionEvent { Type = SessionEventType.Failed, SessionId = sessionId, ParentSessionId = parentSessionId, Iteration = iteration, Error = completionError };
                    yield break;
                }

                _logger.LogInformation("Iteration {iter}: finishReason={reason} toolCalls={count} contentChars={chars}",
                    iteration, finishReason, toolCallAcc.Count, contentBuffer.Length);

                if (toolCallAcc.Count > 0)
                {
                    // Build tool-call list first; OpenAI SDK requires the
                    // assistant message to be constructed with at least one
                    // tool call (or non-empty content) so we cannot start with
                    // an empty list and append later.
                    var toolCalls = new List<ChatToolCall>();
                    foreach (var kv in toolCallAcc)
                    {
                        var acc = kv.Value;
                        if (string.IsNullOrEmpty(acc.Id) || string.IsNullOrEmpty(acc.Name))
                        {
                            _logger.LogWarning("Discarding malformed tool call at index {idx} (id='{id}' name='{name}').", kv.Key, acc.Id, acc.Name);
                            continue;
                        }
                        toolCalls.Add(ChatToolCall.CreateFunctionToolCall(acc.Id, acc.Name, BinaryData.FromString(acc.Arguments.Length == 0 ? "{}" : acc.Arguments.ToString())));
                    }
                    if (toolCalls.Count == 0)
                    {
                        // Streaming reported tool-call updates but none had a complete
                        // id+name pair. Treat as malformed completion and retry.
                        _logger.LogWarning("All streamed tool calls were malformed; retrying iteration {iter}.", iteration);
                        continue;
                    }
                    var assistantMsg = new AssistantChatMessage(toolCalls);
                    var contentText = contentBuffer.ToString();
                    if (!string.IsNullOrEmpty(contentText))
                    {
                        assistantMsg.Content.Add(ChatMessageContentPart.CreateTextPart(contentText));
                    }
                    messages.Add(assistantMsg);

                    foreach (var call in assistantMsg.ToolCalls)
                    {
                        var arguments = call.FunctionArguments?.ToString() ?? "{}";
                        yield return new SessionEvent
                        {
                            Type = SessionEventType.ToolCallStarted,
                            SessionId = sessionId,
                            ParentSessionId = parentSessionId,
                            Iteration = iteration,
                            ToolCallId = call.Id,
                            ToolName = call.FunctionName,
                            Arguments = arguments,
                        };

                        var resolved = resolvedTools.FirstOrDefault(t => string.Equals(t.Name, call.FunctionName, StringComparison.Ordinal));
                        ToolResult toolResult;
                        if (resolved == null)
                        {
                            toolResult = new ToolResult(
                                JsonSerializer.Serialize(new { error = $"Unknown tool '{call.FunctionName}'." }),
                                IsError: true);
                        }
                        else
                        {
                            try
                            {
                                toolResult = await _toolRegistry.ExecuteAsync(resolved, arguments, sessionId, workingDirectory, accessContext, cancellationToken).ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Tool {tool} threw during execution.", call.FunctionName);
                                toolResult = new ToolResult(
                                    JsonSerializer.Serialize(new { error = ex.Message }),
                                    IsError: true);
                            }
                        }
                        messages.Add(new ToolChatMessage(call.Id, toolResult.Content));
                        history?.Add(new SessionMessage
                        {
                            Role = SessionMessageRole.Tool,
                            Content = toolResult.Content,
                            ToolCallId = call.Id,
                            ToolName = call.FunctionName,
                        });

                        yield return new SessionEvent
                        {
                            Type = SessionEventType.ToolCallCompleted,
                            SessionId = sessionId,
                            ParentSessionId = parentSessionId,
                            Iteration = iteration,
                            ToolCallId = call.Id,
                            ToolName = call.FunctionName,
                            Result = toolResult.Content,
                            Error = toolResult.IsError ? toolResult.Content : null,
                        };
                    }
                    continue;
                }

                var text = contentBuffer.ToString();
                history?.Add(new SessionMessage
                {
                    Role = SessionMessageRole.Assistant,
                    Content = text,
                });
                yield return new SessionEvent
                {
                    Type = SessionEventType.Completed,
                    SessionId = sessionId,
                    ParentSessionId = parentSessionId,
                    Iteration = iteration,
                    Answer = text,
                };
                yield break;
            }

            _logger.LogWarning("Agent loop hit MaxIterations={max} for session {sid}.", MaxIterations, sessionId);
            yield return new SessionEvent
            {
                Type = SessionEventType.Failed,
                SessionId = sessionId,
                ParentSessionId = parentSessionId,
                Error = $"Agent stopped after {MaxIterations} iterations without producing a final answer.",
            };
        }

        // Per-iteration accumulator for streaming tool-call fragments.
        private sealed class AccumulatedToolCall
        {
            public string? Id { get; set; }
            public string? Name { get; set; }
            public System.Text.StringBuilder Arguments { get; } = new();
        }
    }
}
