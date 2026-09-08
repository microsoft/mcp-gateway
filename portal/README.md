# MCP Gateway Portal

A React + TypeScript single-page application that lets operators manage the
lifecycle of MCP servers (adapters and tools) registered with the MCP Gateway,
inspect their status and logs, and test their connections directly from the
browser.

## Using the Portal

Open `/portal/` on your gateway. Use **MCP Servers** to create and manage
adapters, and **Tools** to manage registered HTTP tools. Each detail page
provides status, configuration, logs, and a **Test connection** tab.

The test panel supports MCP `2026-07-28` only. **Connect** discovers the server
and lists its tools without initializing a transport session. Select a tool,
enter its arguments, and run it. The portal supplies per-request protocol
metadata and schema-derived headers automatically.

The **Advanced** console accepts JSON-RPC requests. SSE events appear as they
arrive, and **Cancel requests** closes active streams. An `input_required`
result is reported as incomplete; the portal does not perform interactive
continuation. Servers that do not implement `subscriptions/listen` return a
method error instead of a notification stream.

Legacy clients and adapters need a compatible previous gateway version. See
the [migration guide](../docs/mcp-2026-07-28.md) for the request contract and
upgrade requirements.

## Authentication

- **Local / dev mode** — the gateway runs the `DevelopmentAuthenticationHandler`
  which mints a `dev` principal for unauthenticated requests. The portal
  detects `isDevelopment: true` from `/portal/config` and skips MSAL; the user
  can optionally pick a synthetic identity (user id, display name, roles) that
  is forwarded via `X-Dev-UserId`, `X-Dev-Name`, and `X-Dev-Roles` headers so
  RBAC paths can be exercised end to end.
- **Cloud / production mode** — the portal initializes
  [`@azure/msal-browser`](https://www.npmjs.com/package/@azure/msal-browser)
  with the tenant/client id from `/portal/config`, signs the user in via the
  redirect flow, and acquires an access token with the
  `api://<clientId>/.default` scope. Every API call attaches that token as a
  `Bearer` header. Server-side filtering already limits responses to resources
  the principal is allowed to see, so no client-side filtering is required.

## Running Locally

Use Node 22.18 or later. When using the Vite server, add
`http://localhost:5173` to the gateway's `Mcp:AllowedOrigins` setting (for
example, `Mcp__AllowedOrigins__0=http://localhost:5173`). The proxy preserves
the browser's Origin; allowing only the gateway's own origin would reject
MCP POSTs from Vite.

```bash
# from the repo root, after deploying the gateway to localhost:8000
cd portal
npm ci
npm run dev
```

Then open <http://localhost:5173/portal/> (the app uses a `/portal` router
basename, so the root path renders a blank page). Vite proxies `/adapters`,
`/tools`, `/agents`, `/sessions`, `/mcp`, `/portal/config`, and `/ping` to
`http://localhost:8000` so the SPA shares an origin with the gateway. Override
the target with `VITE_GATEWAY_URL=http://example:8000 npm run dev`.

### Gateway storage in dev mode

When `ASPNETCORE_ENVIRONMENT=Development` the gateway probes the configured
Redis at startup (default `redis-service:6379` from
`appsettings.Development.json`, which only resolves inside the k8s deployment).
If the connection succeeds the Redis-backed adapter and tool stores are used
exactly as in the k8s local deployment; if it fails — for example a vanilla
`dotnet run` on a laptop — the gateway transparently falls back to in-memory
stores so the management portal works without any external dependencies. Force
either path by setting `Storage__UseInMemoryStores=true|false` in the process
environment or `Storage:UseInMemoryStores` in configuration. The Kubernetes
local manifest explicitly sets this to `false` so gateway replicas share the
Redis resource store regardless of startup order.

## Building for production

```bash
cd portal
npm ci
npm run build
```

The output lands in `dotnet/Microsoft.McpGateway.Service/src/wwwroot/portal/`. The
service's csproj invokes the same commands automatically during
`dotnet publish`, so the container image produced by the existing GitHub
Actions workflow already includes the portal.

## Tests

Run `npm test` from this directory for protocol-header and incremental SSE
tests. `npm run build` also typechecks the portal. See the
[end-to-end guide](../deployment/e2e/README.md) to test against a Kubernetes
deployment.
