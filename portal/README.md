# MCP Gateway Portal

A React + TypeScript single-page application that lets operators manage the
lifecycle of MCP servers (adapters and tools) registered with the MCP Gateway,
inspect their status and logs, and test their connections directly from the
browser.

## Architecture

- The portal is a standard Vite + React SPA written in TypeScript and styled
  with Fluent UI v9.
- In production the bundle is built into
  `dotnet/Microsoft.McpGateway.Service/src/wwwroot/` and served by the gateway
  itself, so the SPA and the API share a single origin and no CORS rules are
  needed.
- At startup the portal loads `GET /portal/config` (anonymous) to learn whether
  the gateway is running in development or cloud mode and, in cloud mode, which
  Entra ID tenant and audience to authenticate against. The same endpoint
  returns its self-identifying `publicOrigin` so the SPA can construct MCP
  endpoint URLs that work from anywhere it's hosted.

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

## Running locally

```bash
# from the repo root, after deploying the gateway to localhost:8000
cd portal
npm install
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
either path by setting `Storage:UseInMemoryStores=true|false` (env var or
appsettings override).

## Building for production

```bash
cd portal
npm install
npm run build
```

The output lands in `dotnet/Microsoft.McpGateway.Service/src/wwwroot/`. The
service's csproj invokes the same commands automatically during
`dotnet publish`, so the container image produced by the existing GitHub
Actions workflow already includes the portal.

## Layout

| Path                                  | Purpose                                                   |
| ------------------------------------- | --------------------------------------------------------- |
| `src/main.tsx`                        | App entrypoint, Fluent UI provider, MSAL bootstrap        |
| `src/auth/`                           | Runtime config loader + MSAL helpers + dev identity store |
| `src/api/`                            | Typed fetch client mirroring the gateway contracts        |
| `src/pages/AdaptersPage.tsx`          | List / search MCP adapters                                |
| `src/pages/AdapterDetailPage.tsx`     | View metadata, status, logs; embeds the MCP test panel    |
| `src/pages/AdapterCreatePage.tsx`     | Form for creating or editing adapters                     |
| `src/pages/ToolsPage.tsx`             | List / search registered tools                            |
| `src/pages/ToolDetailPage.tsx`        | View metadata, status, logs, tool definition              |
| `src/pages/ToolCreatePage.tsx`        | Form for creating or editing tools                        |
| `src/components/McpTestPanel.tsx`     | JSON-RPC test console (`initialize`, `tools/list`, …)     |
| `src/components/Layout.tsx`           | Top bar, sign-in state, dev-identity switcher             |
