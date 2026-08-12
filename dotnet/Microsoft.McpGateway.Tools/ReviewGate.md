# Optional: pre-execution judgment gate for the tool gateway

`HttpToolExecutor` (this project's built-in "toolgateway" adapter) checks `Operation.Read`
RBAC on the registered `ToolResource` before forwarding a tool call's arguments verbatim to
its execution endpoint. That answers *"is this caller allowed to invoke this tool at all"* --
it does not look at *"is this specific call, with these specific arguments, actually safe to
run right now"*. `AdapterReverseProxyController`'s own separate proxy path is stricter still:
it's a byte-blind reverse proxy that never deserializes the request body, so it has no way to
apply a content-level check even in principle.

`ReviewGatedToolExecutor` is an optional `IToolExecutor` decorator that closes that specific
gap for the toolgateway path: it calls an independent judgment endpoint with the tool name and
arguments, blocks on a clean high-confidence `reject`, and falls through to the real executor
on everything else (approve, low-confidence/ambiguous reject, or the gate being unavailable --
see the fail-open design note in `src/Services/ReviewGatedToolExecutor.cs`). It has no
knowledge of which judgment provider is behind the HTTP call; the shape only assumes a
`POST {baseUrl}/review` returning `{ "verdict": "approve" | "approve_with_concerns" |
"reject", "confidence": number, "summary": string }`.

**Disabled by default** -- identical behavior to before unless explicitly turned on.

```json
{
  "ReviewGate": {
    "Enabled": true,
    "BaseUrl": "https://api.babyblueviper.com",
    "ApiKey": "<your key>"
  }
}
```

or via environment variables: `ReviewGate__Enabled=true`, `ReviewGate__BaseUrl=...`,
`ReviewGate__ApiKey=...`.

## Tests

`test/ReviewGatedToolExecutorTests.cs` -- offline, MSTest + a minimal fake `HttpMessageHandler`
(no new test-only NuGet dependency): confirms a high-confidence `reject` blocks and never calls
the inner executor, an `approve` (and a *low*-confidence `reject`) fall through, and a
non-2xx/timeout from the gate itself fails open.

Live-verified against a real judgment endpoint (not part of this PR's test run, no network
calls in CI): a benign `list_files` call fell through to the inner executor unmodified, and a
`run_shell` call with `rm -rf / --no-preserve-root` was blocked with
`verdict=reject, confidence=1.00`. Reference client + a runnable live-verification harness:
https://github.com/babyblueviper1/invinoveritas/tree/main/integrations/mcp-gateway
