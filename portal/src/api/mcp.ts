export const MCP_VERSION = "2026-07-28";

export function isRecord(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}

export function encodeMcpHeader(value: string): string {
  if (/^[\x20-\x7e\t]*$/.test(value) && value.trim() === value &&
      !(value.startsWith("=?base64?") && value.endsWith("?="))) return value;
  const bytes = new TextEncoder().encode(value);
  return `=?base64?${btoa(Array.from(bytes, byte => String.fromCharCode(byte)).join(""))}?=`;
}

export function toolHeaders(schema: unknown, argumentsValue: unknown): Record<string, string> {
  const headers: Record<string, string> = {};
  const names = new Set<string>();
  const walk = (node: unknown, value: unknown, reachable: boolean, property: boolean) => {
    if (!isRecord(node)) return;
    if ("x-mcp-header" in node) {
      const name = node["x-mcp-header"];
      const types = Array.isArray(node.type) ? node.type : [node.type];
      const primitives = types.filter(type => type !== "null");
      if (!reachable || !property || typeof name !== "string" ||
          !/^[!#$%&'*+.^_`|~a-zA-Z0-9-]+$/.test(name) || names.has(name.toLowerCase()) ||
          primitives.length !== 1 || !["string", "integer", "boolean"].includes(String(primitives[0]))) {
        throw new Error("Invalid x-mcp-header annotation in tool schema.");
      }
      names.add(name.toLowerCase());
      if (value !== undefined && value !== null) {
        const type = primitives[0];
        if (type === "integer" ? !Number.isSafeInteger(value) : typeof value !== type) {
          throw new Error(`Invalid value for mirrored header Mcp-Param-${name}.`);
        }
        headers[`Mcp-Param-${name}`] = encodeMcpHeader(String(value));
      }
    }
    for (const [key, child] of Object.entries(node)) {
      if (key === "properties" && isRecord(child)) {
        for (const [name, definition] of Object.entries(child)) {
          walk(definition, isRecord(value) ? value[name] : undefined, reachable, true);
        }
      } else if (key !== "default" && key !== "examples" && key !== "enum" && key !== "const") {
        if (Array.isArray(child)) child.forEach(item => walk(item, undefined, false, false));
        else if (isRecord(child)) {
          walk(child, undefined, false, false);
          if (key === "$defs" || key === "definitions") {
            Object.values(child).forEach(item => walk(item, undefined, false, false));
          }
        }
      }
    }
  };
  walk(schema, argumentsValue, true, false);
  return headers;
}

export function buildMcpRequest(body: unknown, inputSchema?: unknown) {
  if (!isRecord(body) || body.jsonrpc !== "2.0" || typeof body.method !== "string") {
    throw new Error("A JSON-RPC 2.0 request is required.");
  }
  if (["initialize", "notifications/initialized", "ping", "logging/setLevel", "resources/subscribe", "resources/unsubscribe"].includes(body.method)) {
    throw new Error(`${body.method} is not supported by MCP ${MCP_VERSION}. Upgrade the client or use the legacy gateway image.`);
  }
  const params = isRecord(body.params) ? body.params : {};
  const headers: Record<string, string> = {
    "MCP-Protocol-Version": MCP_VERSION,
    "Mcp-Method": body.method,
  };
  if (["tools/call", "resources/read", "prompts/get"].includes(body.method)) {
    const name = body.method === "resources/read" ? params.uri : params.name;
    if (typeof name !== "string") throw new Error("This method requires a name or resource URI.");
    headers["Mcp-Name"] = encodeMcpHeader(name);
  }
  if (body.method === "tools/call") Object.assign(headers, toolHeaders(inputSchema, params.arguments));
  return {
    headers,
    body: {
      ...body,
      params: {
        ...params,
        _meta: {
          ...(isRecord(params._meta) ? params._meta : {}),
          "io.modelcontextprotocol/protocolVersion": MCP_VERSION,
          "io.modelcontextprotocol/clientInfo": { name: "mcp-gateway-portal", version: "1.0.0" },
          "io.modelcontextprotocol/clientCapabilities": {},
        },
      },
    },
  };
}