import assert from "node:assert/strict";
import { buildMcpRequest, MCP_VERSION } from "../../portal/src/api/mcp.ts";
import { readMcpBody } from "../../portal/src/api/mcpStream.ts";

const baseUrl = process.env.MCP_BASE_URL ?? "http://localhost:18000";
const namespace = process.env.MCP_NAMESPACE ?? "mcp-modern-e2e";
const imageTag = process.env.MCP_IMAGE_TAG ?? "e2e";
const token = process.env.MCP_E2E_TOKEN;
const phase = process.argv[2] ?? "verify";
const ownerHeaders = token ? { Authorization: `Bearer ${token}` } : { "X-Dev-UserId": "e2e-owner", "X-Dev-Roles": "mcp.admin" };
const passed = [];
let requestId = 100;

if (token && !baseUrl.startsWith("https://")) throw new Error("Cloud bearer tokens require an HTTPS endpoint.");

async function check(name, action) {
  await action();
  passed.push(name);
  console.log(`PASS ${name}`);
}

async function request(path, { body, method = "GET", headers = {}, authenticated = true, signal } = {}) {
  return fetch(new URL(path, baseUrl), {
    method, signal: signal ?? AbortSignal.timeout(30000),
    headers: { ...(authenticated ? ownerHeaders : {}), ...(body ? { "Content-Type": "application/json" } : {}), ...headers },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
}

async function rpc(path, method, params = {}, options = {}) {
  const id = ++requestId;
  const wire = buildMcpRequest({ jsonrpc: "2.0", id, method, params }, options.schema);
  if (options.bodyVersion) wire.body.params._meta["io.modelcontextprotocol/protocolVersion"] = options.bodyVersion;
  const response = await request(path, {
    method: "POST", body: wire.body,
    headers: { Accept: "application/json, text/event-stream", ...wire.headers, ...options.headers },
  });
  const parsed = await readMcpBody(response);
  const message = Array.isArray(parsed) ? parsed.find(item => item?.id === id) : parsed;
  return { response, message };
}

function complete(reply) {
  assert.equal(reply.response.status, 200, JSON.stringify(reply.message));
  assert.ok(reply.message?.result, JSON.stringify(reply.message));
  assert.equal(reply.message.result.resultType, "complete");
  assert.equal(reply.response.headers.get("mcp-session-id"), null);
  return reply.message.result;
}

const weatherSchema = {
  type: "object", properties: { location: { type: "string", "x-mcp-header": "Location" } }, required: ["location"],
};
const weather = {
  name: "e2e-weather", imageName: "weather-tool", imageVersion: imageTag, requiredRoles: ["mcp.reader"],
  replicaCount: 1, useWorkloadIdentity: false, environmentVariables: {},
  toolDefinition: { tool: { name: "e2e-weather", description: "E2E weather fixture", inputSchema: weatherSchema }, port: 8000 },
};

if (phase === "setup") {
  for (const [path, body] of [
    ["/adapters", { name: "e2e-example", imageName: "mcp-example", imageVersion: imageTag, replicaCount: 2, useWorkloadIdentity: false, requiredRoles: [], environmentVariables: {} }],
    ["/adapters", { name: "e2e-proxy", imageName: "mcp-proxy", imageVersion: imageTag, replicaCount: 1, useWorkloadIdentity: false, requiredRoles: [],
      environmentVariables: { MCP_PROXY_URL: `http://e2e-example-service.${namespace}.svc.cluster.local:8000/mcp` } }],
    ["/adapters", { name: "e2e-stream", imageName: "mcp-stream-fixture", imageVersion: imageTag, replicaCount: 1, useWorkloadIdentity: false, requiredRoles: [], environmentVariables: {} }],
    ["/tools", weather],
  ]) {
    await check(`register ${body.name}`, async () => {
      const existing = await request(`${path}/${body.name}`);
      if (existing.status === 200) {
        const resource = await existing.json();
        assert.equal(resource.imageVersion, imageTag, "Existing fixture uses a different image tag");
        return;
      }
      assert.equal(existing.status, 404, `Cannot inspect test fixture: ${existing.status}`);
      const response = await request(path, { method: "POST", body });
      assert.ok(response.ok, `${response.status}: ${await response.text()}`);
    });
  }
} else {
  await check("gateway health", async () => assert.equal((await request("/ping")).status, 200));

  for (const path of ["/mcp", "/adapters/e2e-example/mcp", "/adapters/e2e-proxy/mcp"]) {
    await check(`${path}: direct tools/list before discovery`, async () => {
      const result = complete(await rpc(path, "tools/list"));
      assert.ok(result.tools.length > 0);
      assert.ok(result.ttlMs >= 0);
      assert.ok(["private", "public"].includes(result.cacheScope));
    });
    await check(`${path}: discovery`, async () => {
      const result = complete(await rpc(path, "server/discover"));
      assert.ok(result.supportedVersions.includes(MCP_VERSION));
      assert.ok(result.ttlMs >= 0);
    });
    await check(`${path}: ignores bogus legacy transport context`, async () => {
      complete(await rpc(`${path}?session_id=not:a:session&application=value`, "tools/list", {}, {
        headers: { "Mcp-Session-Id": "other:user:session", "Last-Event-ID": "not-resumable" },
      }));
    });
    await check(`${path}: modern unknown-method error`, async () => {
      const reply = await rpc(path, "unknown/method");
      assert.equal(reply.response.status, 404, JSON.stringify(reply.message));
      assert.equal(reply.message.error.code, -32601);
    });
    await check(`${path}: header/body version mismatch`, async () => {
      const reply = await rpc(path, "tools/list", {}, { bodyVersion: "2025-11-25" });
      assert.equal(reply.response.status, 400, JSON.stringify(reply.message));
      assert.equal(reply.message.error.code, -32020);
    });
    await check(`${path}: rejects legacy initialize`, async () => {
      const response = await request(path, { method: "POST", headers: { "MCP-Protocol-Version": "2025-06-18", "Mcp-Method": "initialize" },
        body: { jsonrpc: "2.0", id: ++requestId, method: "initialize", params: { protocolVersion: "2025-06-18", capabilities: {} } } });
      assert.equal(response.status, 400);
      assert.equal((await response.json()).error.code, -32022);
    });
    await check(`${path}: rejects GET and DELETE`, async () => {
      for (const method of ["GET", "DELETE"]) {
        const response = await request(path, { method });
        assert.equal(response.status, 405);
        assert.equal(response.headers.get("allow"), "POST");
      }
    });
    await check(`${path}: disallowed Origin`, async () => {
      const response = await request(path, { method: "POST", headers: { Origin: "https://not-allowed.example" }, body: {} });
      assert.equal(response.status, 403);
    });
  }

  for (const path of ["/adapters/e2e-example/mcp", "/adapters/e2e-proxy/mcp"]) {
    await check(`${path}: tool execution`, async () => {
      const list = complete(await rpc(path, "tools/list"));
      const tool = list.tools.find(item => item.name === "add" || item.name.endsWith("_add"));
      assert.ok(tool, "Expected the add tool through the sample/proxy");
      for (let attempt = 0; attempt < 6; attempt++) {
        const result = complete(await rpc(path, "tools/call", { name: tool.name, arguments: { a: attempt, b: 5 } }, { schema: tool.inputSchema }));
        assert.notEqual(result.isError, true);
        assert.equal(Number(result.content.find(item => item.type === "text").text), attempt + 5);
      }
    });
  }

  await check("first-party private deterministic catalog", async () => {
    const result = complete(await rpc("/mcp", "tools/list"));
    assert.equal(result.cacheScope, "private");
    assert.equal(result.ttlMs, 0);
    const names = result.tools.map(tool => tool.name);
    assert.deepEqual(names, [...names].sort());
  });
  await check("dynamic tool execution with mirrored headers", async () => {
    const result = complete(await rpc("/mcp", "tools/call", { name: weather.name, arguments: { location: "Seattle" } }, { schema: weatherSchema }));
    assert.notEqual(result.isError, true, JSON.stringify(result));
    assert.match(result.content[0].text, /Seattle/);
  });
  await check("dynamic header mismatch rejected before execution", async () => {
    const reply = await rpc("/mcp", "tools/call", { name: weather.name, arguments: { location: "Seattle" } },
      { schema: weatherSchema, headers: { "Mcp-Param-Location": "wrong" } });
    assert.equal(reply.response.status, 400, JSON.stringify(reply.message));
    assert.equal(reply.message.error.code, -32020);
  });

  if (!token) {
    const other = { "X-Dev-UserId": "e2e-other", "X-Dev-Roles": "mcp.dev" };
    await check("unauthorized adapter read denied despite spoofed identity/session", async () => {
      const reply = await rpc("/adapters/e2e-example/mcp", "tools/list", {}, { headers: { ...other,
        "X-User-Id": "e2e-owner", "Mcp-Session-Id": "e2e-owner:session" } });
      assert.equal(reply.response.status, 403);
    });
    await check("catalog isolated between callers", async () => {
      const result = complete(await rpc("/mcp", "tools/list", {}, { headers: other }));
      assert.equal(result.cacheScope, "private");
      assert.equal(result.tools.length, 0);
    });
    await check("permission revocation blocks execution despite cached catalog", async () => {
      const reader = { "X-Dev-UserId": "e2e-reader", "X-Dev-Roles": "mcp.reader" };
      const result = complete(await rpc("/mcp", "tools/list", {}, { headers: reader }));
      assert.ok(result.tools.some(tool => tool.name === weather.name));
      const updated = await request(`/tools/${weather.name}`, { method: "PUT", body: { ...weather, requiredRoles: [] } });
      assert.ok(updated.ok, await updated.text());
      try {
        const denied = complete(await rpc("/mcp", "tools/call", { name: weather.name, arguments: { location: "Seattle" } }, { schema: weatherSchema, headers: reader }));
        assert.equal(denied.isError, true);
      } finally {
        const restored = await request(`/tools/${weather.name}`, { method: "PUT", body: weather });
        assert.ok(restored.ok, await restored.text());
      }
    });
  } else {
    await check("cloud unauthenticated access denied", async () => {
      const response = await request("/adapters", { authenticated: false });
      assert.equal(response.status, 401);
    });
  }

  await check("subscription acknowledgement, quiet interval, and downstream cancellation", async () => {
    const abort = new AbortController();
    const wire = buildMcpRequest({ jsonrpc: "2.0", id: ++requestId, method: "subscriptions/listen", params: { notifications: { toolsListChanged: true } } });
    const opened = performance.now();
    const response = await request("/adapters/e2e-stream/mcp", { method: "POST", body: wire.body,
      headers: { ...wire.headers, Accept: "application/json, text/event-stream" }, signal: abort.signal });
    assert.equal(response.status, 200);
    assert.equal(response.headers.get("content-type")?.split(";")[0], "text/event-stream");
    if (!token) assert.equal(response.headers.get("x-accel-buffering"), "no");
    let acknowledged = false;
    let changed = false;
    const timeout = setTimeout(() => abort.abort(), 45000);
    try {
      await readMcpBody(response, event => {
        assert.equal(event?.params?._meta?.["io.modelcontextprotocol/subscriptionId"], wire.body.id);
        if (event?.method === "notifications/subscriptions/acknowledged") {
          assert.equal(event.params.notifications.toolsListChanged, true);
          assert.ok(performance.now() - opened < 5000, "Acknowledgement must be delivered before the quiet interval ends");
          acknowledged = true;
        }
        if (event?.method === "notifications/tools/list_changed") {
          assert.equal(acknowledged, true);
          assert.ok(performance.now() - opened >= 25000, "Fixture must exercise a quiet interval beyond the old ingress timeout");
          changed = true;
          abort.abort();
        }
      }, abort.signal);
    } catch (error) {
      assert.equal(error.name, "AbortError");
    } finally { clearTimeout(timeout); }
    assert.equal(acknowledged, true, "Subscription did not acknowledge while the stream was open");
    assert.equal(changed, true, "Stream did not survive its quiet interval");
    const cancellationDeadline = performance.now() + 2000;
    let state;
    do {
      const result = complete(await rpc("/adapters/e2e-stream/mcp", "tools/call", { name: "stream_state", arguments: {} }));
      state = JSON.parse(result.content[0].text);
    } while (state.active !== 0 && performance.now() < cancellationDeadline);
    assert.equal(state.active, 0, "Client abort must cancel the downstream subscription");
    assert.ok(state.cancelled >= 1);
  });
}

console.log(JSON.stringify({ phase, endpoint: baseUrl, passed: passed.length, checks: passed }, null, 2));