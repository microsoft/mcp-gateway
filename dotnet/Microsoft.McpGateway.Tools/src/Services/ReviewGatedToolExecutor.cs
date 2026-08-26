// Copyright (c) invinoveritas.
// Reference implementation for microsoft/mcp-gateway -- not part of Microsoft's own repo.
//
// WHY THIS EXISTS (verified against the actual mcp-gateway source, not assumed from docs):
//
// mcp-gateway exposes tool calls through TWO execution paths, and neither carries a
// content-level judgment gate today:
//
//   1. AdapterReverseProxyController.ForwardStreamableHttpRequest -- a byte-blind reverse
//      proxy. HttpProxy.CreateProxiedHttpRequest wraps the raw request body in a
//      `StreamContent` and never deserializes it; RBAC (`IPermissionProvider.CheckAccessAsync`)
//      is checked once, at the ADAPTER level (Operation.Read on the target adapter resource),
//      before any MCP method/tool name is even parsed.
//
//   2. Microsoft.McpGateway.Tools' own built-in "toolgateway" adapter --
//      HttpToolExecutor.ExecuteToolAsync DOES receive the parsed
//      RequestContext<CallToolRequestParams> (tool name + arguments), but only checks
//      `Operation.Read` RBAC on the ToolResource before forwarding the raw arguments
//      byte-for-byte to `{toolName}-service.adapter.svc.cluster.local`. No inspection of
//      WHAT the call actually does.
//
// Both paths were read directly from a fresh clone of microsoft/mcp-gateway (main,
// 2026-08-04, pushed 2026-08-02) before writing this -- not assumed from the README.
//
// THE EXTENSION POINT: `IToolExecutor` (Microsoft.McpGateway.Tools.Contracts) is registered
// via a single DI line in Program.cs --
//   builder.Services.AddSingleton<IToolExecutor, HttpToolExecutor>();
// -- and `WithCallToolHandler` delegates straight to it. That's a real, already-shipped
// decoration point: swap the registration for a wrapper that gates on an independent verdict,
// then falls through to the real executor. Zero changes to mcp-gateway's own source files --
// same shape as every other invinoveritas integration on this list (AgentScope's
// on_check_permission middleware, Qwen-Agent's confirm_callback, LlamaIndex's
// InputRequiredEvent/HumanResponseEvent pair, Vercel AI SDK's toolApproval callback).
//
// DESIGN NOTE -- fail-open on uncertainty, by deliberate choice, not oversight: mcp-gateway is
// a Kubernetes-oriented, fully-automated execution path with no interactive
// human-in-the-loop surface anywhere in the codebase (unlike AgentScope's ASK state or
// LlamaIndex's wait_for_event). There is nowhere to hand an "uncertain" verdict for a human to
// resolve. This decorator therefore only ever BLOCKS on a clean, high-confidence `reject` --
// everything else (approve, approve_with_concerns, low-confidence reject, or the gate itself
// being unavailable) falls through to the real executor. An adopter with a downstream
// escalation surface of their own should tighten `RejectConfidenceThreshold` and/or treat
// `review_unavailable` as a block instead -- both are one-line changes, called out below.
//
// GOTCHA CARRIED FORWARD from the Vercel AI SDK integration (target #19 on
// BIG_SYSTEMS_TARGET_LIST.md): a `/review` call with `sign: true` adds real proof-generation
// latency. That integration's default 5000ms timeout silently resolved to a fail-open
// "allow" that looked identical to a deliberate approve. This decorator defaults `sign` to
// `false` (no proof needed for a per-call runtime gate) specifically to avoid that latency,
// and still gives itself a 15s timeout budget above whatever mcp-gateway's own client uses --
// documented here so nobody re-discovers the same gotcha the hard way.

using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.McpGateway.Tools.Contracts;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Microsoft.McpGateway.Tools.Services
{
    /// <summary>
    /// Configuration for <see cref="ReviewGatedToolExecutor"/>.
    /// </summary>
    public sealed class ReviewGateOptions
    {
        /// <summary>invinoveritas <c>artifact_type</c> sent with every call. Must be one of the
        /// API's fixed enum values (verified live against the production API's own 422 error
        /// message: code_diff, patch, shell_command, plan, config_change, analysis,
        /// agent_output, trade, onchain_action, sanctions_screening, general).</summary>
        public string ArtifactType { get; init; } = "general";

        /// <summary>Only BLOCK when the verdict is "reject" AND confidence is at or above this
        /// threshold. Below it, the call falls through (fail-open on genuine uncertainty).</summary>
        public double RejectConfidenceThreshold { get; init; } = 0.7;

        /// <summary>Per-call budget. See the file-level comment: keep this generous, a judgment
        /// call takes real inference time.</summary>
        public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);

        /// <summary>Attach a signed, independently-verifiable proof to the verdict (costs extra
        /// latency; see the gotcha note above). Default off for a runtime gate.</summary>
        public bool Sign { get; init; } = false;

        /// <summary>Base URL of the invinoveritas API.</summary>
        public string BaseUrl { get; init; } = "https://api.babyblueviper.com";
    }

    /// <summary>
    /// Decorates any <see cref="IToolExecutor"/> with an independent, out-of-band judgment
    /// verdict before the wrapped executor actually runs the tool. See the file-level comment
    /// for the exact gap this closes and why the design fails open on uncertainty.
    /// </summary>
    public sealed class ReviewGatedToolExecutor : IToolExecutor
    {
        private readonly IToolExecutor innerExecutor;
        private readonly HttpClient httpClient;
        private readonly ILogger<ReviewGatedToolExecutor> logger;
        private readonly ReviewGateOptions options;

        /// <param name="innerExecutor">The real executor to gate -- typically the DI-registered
        /// <c>HttpToolExecutor</c>, or any other <see cref="IToolExecutor"/>.</param>
        /// <param name="httpClient">An <see cref="HttpClient"/> whose <c>BaseAddress</c> and
        /// <c>Authorization</c> header the caller has already configured (see README.md for the
        /// exact DI wiring -- this class deliberately takes no opinion on API-key sourcing).</param>
        public ReviewGatedToolExecutor(
            IToolExecutor innerExecutor,
            HttpClient httpClient,
            ILogger<ReviewGatedToolExecutor> logger,
            ReviewGateOptions? options = null)
        {
            this.innerExecutor = innerExecutor ?? throw new ArgumentNullException(nameof(innerExecutor));
            this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            this.options = options ?? new ReviewGateOptions();
        }

        /// <inheritdoc/>
        public async ValueTask<CallToolResult> ExecuteToolAsync(
            RequestContext<CallToolRequestParams> requestContext,
            CancellationToken cancellationToken)
        {
            var toolName = requestContext.Params?.Name ?? "(unknown)";
            var artifact = JsonSerializer.Serialize(new
            {
                tool = toolName,
                input = requestContext.Params?.Arguments,
            });

            var review = await this.TryGetVerdictAsync(toolName, artifact, cancellationToken).ConfigureAwait(false);

            if (review is { Verdict: "reject" } && review.Confidence.GetValueOrDefault() >= this.options.RejectConfidenceThreshold)
            {
                this.logger.LogWarning(
                    "invinoveritas /review DENIED tool {ToolName} (confidence {Confidence}): {Summary}",
                    toolName,
                    review.Confidence,
                    review.Summary);

                return new CallToolResult
                {
                    IsError = true,
                    Content =
                    [
                        new TextContentBlock
                        {
                            Text = $"Blocked by invinoveritas /review gate (verdict=reject, confidence={review.Confidence:0.00}): {review.Summary}",
                        },
                    ],
                };
            }

            // approve / approve_with_concerns / sub-threshold reject / gate unavailable: fall
            // through to the real executor. See the file-level "fail-open on uncertainty" note.
            return await this.innerExecutor.ExecuteToolAsync(requestContext, cancellationToken).ConfigureAwait(false);
        }

        private async Task<ReviewResponse?> TryGetVerdictAsync(string toolName, string artifact, CancellationToken cancellationToken)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(this.options.Timeout);

                var payload = new
                {
                    artifact,
                    artifact_type = this.options.ArtifactType,
                    context = $"mcp-gateway tool-call gate for tool \"{toolName}\".",
                    sign = this.options.Sign,
                };

                using var response = await this.httpClient
                    .PostAsJsonAsync("/review", payload, cts.Token)
                    .ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    this.logger.LogWarning(
                        "invinoveritas /review returned {StatusCode} for tool {ToolName}; falling through (fail-open).",
                        response.StatusCode,
                        toolName);
                    return null;
                }

                return await response.Content
                    .ReadFromJsonAsync<ReviewResponse>(cancellationToken: cts.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or OperationCanceledException)
            {
                this.logger.LogWarning(
                    ex,
                    "invinoveritas /review unavailable for tool {ToolName}; falling through (fail-open).",
                    toolName);
                return null;
            }
        }

        private sealed class ReviewResponse
        {
            [JsonPropertyName("verdict")]
            public string? Verdict { get; set; }

            [JsonPropertyName("confidence")]
            public double? Confidence { get; set; }

            [JsonPropertyName("summary")]
            public string? Summary { get; set; }
        }
    }
}
