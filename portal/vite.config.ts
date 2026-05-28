// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import path from "node:path";

// The portal is built into the .NET service's wwwroot folder so a single
// container image can serve both the API and the SPA. During `vite dev`
// we proxy API/MCP calls to the local gateway so requests share an origin.
const GATEWAY_DEV_TARGET = process.env.VITE_GATEWAY_URL ?? "http://localhost:8000";

// Endpoints that must reach the gateway, not the Vite dev server.
const proxyPaths = ["/portal/config", "/adapters", "/tools", "/agents", "/sessions", "/mcp", "/ping"];

const proxy = Object.fromEntries(
  proxyPaths.map((prefix) => [
    prefix,
    {
      target: GATEWAY_DEV_TARGET,
      changeOrigin: true,
      ws: true,
    },
  ]),
);

export default defineConfig({
  plugins: [react()],
  // The SPA is mounted under /portal/ in the gateway so deep links to
  // /adapters, /tools, etc. continue to hit the API controllers and the
  // SPA owns its own route subtree without conflicting with them.
  base: "/portal/",
  server: {
    port: 5173,
    proxy,
  },
  build: {
    outDir: path.resolve(
      __dirname,
      "../dotnet/Microsoft.McpGateway.Service/src/wwwroot/portal",
    ),
    emptyOutDir: true,
    sourcemap: false,
    target: "es2022",
  },
});
