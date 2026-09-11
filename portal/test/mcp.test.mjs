import assert from "node:assert/strict";
import test from "node:test";
import { buildMcpRequest, encodeMcpHeader, MCP_VERSION, toolHeaders } from "../src/api/mcp.ts";

test("every request has modern metadata without a handshake", () => {
  const request = buildMcpRequest({ jsonrpc: "2.0", id: 1, method: "server/discover" });
  assert.equal(request.headers["MCP-Protocol-Version"], MCP_VERSION);
  assert.equal(request.body.params._meta["io.modelcontextprotocol/protocolVersion"], MCP_VERSION);
  assert.deepEqual(request.body.params._meta["io.modelcontextprotocol/clientCapabilities"], {});
  assert.equal(request.headers["Mcp-Session-Id"], undefined);
});

test("legacy methods never trigger fallback", () => {
  for (const method of ["initialize", "notifications/initialized", "ping"]) {
    assert.throws(() => buildMcpRequest({ jsonrpc: "2.0", id: 1, method }), /not supported/);
  }
});

test("header encoding preserves sentinel-like and padded values", () => {
  for (const value of [" padded ", "=?base64?literal?=", "line\nbreak", "\u00e9"]) {
    const encoded = encodeMcpHeader(value);
    assert.equal(Buffer.from(encoded.slice(9, -2), "base64").toString("utf8"), value);
  }
  assert.equal(encodeMcpHeader("weather"), "weather");
});

test("nested parameters are mirrored and optional nulls omitted", () => {
  const schema = { type: "object", properties: {
    settings: { type: "object", properties: { region: { type: "string", "x-mcp-header": "Region" } } },
    count: { type: ["integer", "null"], "x-mcp-header": "Count" },
  } };
  const request = buildMcpRequest({ jsonrpc: "2.0", id: 1, method: "tools/call", params: {
    name: "weather", arguments: { settings: { region: "westus2" }, count: null },
  } }, schema);
  assert.equal(request.headers["Mcp-Name"], "weather");
  assert.equal(request.headers["Mcp-Param-Region"], "westus2");
  assert.equal(request.headers["Mcp-Param-Count"], undefined);
});

test("invalid annotations and unsafe integers are rejected", () => {
  for (const schema of [
    { properties: { amount: { type: "number", "x-mcp-header": "Amount" } } },
    { properties: { first: { type: "string", "x-mcp-header": "X" }, second: { type: "string", "x-mcp-header": "x" } } },
    { anyOf: [{ properties: { name: { type: "string", "x-mcp-header": "Name" } } }] },
  ]) assert.throws(() => toolHeaders(schema, {}), /Invalid/);
  assert.throws(() => toolHeaders({ properties: { count: { type: "integer", "x-mcp-header": "Count" } } },
    { count: Number.MAX_SAFE_INTEGER + 1 }), /Invalid/);
});