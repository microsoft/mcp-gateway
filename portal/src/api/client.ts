// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

import type {
  AdapterData,
  AdapterResource,
  AdapterStatus,
  PortalConfig,
  ToolData,
  ToolResource,
} from "./types";
import { acquireToken } from "../auth/msal";
import { devIdentityHeaders, loadDevIdentity } from "../auth/devIdentity";

export class ApiError extends Error {
  status: number;
  body: string;

  constructor(status: number, message: string, body: string) {
    super(message);
    this.status = status;
    this.body = body;
  }
}

interface RequestOptions {
  method?: string;
  body?: unknown;
  query?: Record<string, string | number | undefined>;
  /** When true, attach the bearer/dev headers; defaults to true. */
  authenticated?: boolean;
  signal?: AbortSignal;
  /** Override Accept header for streaming endpoints (e.g. SSE). */
  accept?: string;
}

/**
 * Lightweight wrapper around `fetch` that adds auth headers based on the
 * runtime configuration. We deliberately keep this small: every controller in
 * the gateway returns plain JSON with sensible status codes, so a single
 * `request` helper handles all CRUD calls.
 */
export class GatewayApi {
  constructor(private readonly config: PortalConfig) {}

  // ---- Adapters -------------------------------------------------------

  listAdapters(signal?: AbortSignal): Promise<AdapterResource[]> {
    return this.request<AdapterResource[]>("/adapters", { signal });
  }

  getAdapter(name: string, signal?: AbortSignal): Promise<AdapterResource> {
    return this.request<AdapterResource>(`/adapters/${encodeURIComponent(name)}`, { signal });
  }

  getAdapterStatus(name: string, signal?: AbortSignal): Promise<AdapterStatus> {
    return this.request<AdapterStatus>(
      `/adapters/${encodeURIComponent(name)}/status`,
      { signal },
    );
  }

  getAdapterLogs(name: string, instance = 0, signal?: AbortSignal): Promise<unknown> {
    return this.request<unknown>(`/adapters/${encodeURIComponent(name)}/logs`, {
      query: { instance },
      signal,
    });
  }

  createAdapter(payload: AdapterData): Promise<AdapterResource> {
    return this.request<AdapterResource>("/adapters", { method: "POST", body: payload });
  }

  updateAdapter(payload: AdapterData): Promise<AdapterResource> {
    return this.request<AdapterResource>(
      `/adapters/${encodeURIComponent(payload.name)}`,
      { method: "PUT", body: payload },
    );
  }

  deleteAdapter(name: string): Promise<void> {
    return this.request<void>(`/adapters/${encodeURIComponent(name)}`, { method: "DELETE" });
  }

  // ---- Tools ----------------------------------------------------------

  listTools(signal?: AbortSignal): Promise<ToolResource[]> {
    return this.request<ToolResource[]>("/tools", { signal });
  }

  getTool(name: string, signal?: AbortSignal): Promise<ToolResource> {
    return this.request<ToolResource>(`/tools/${encodeURIComponent(name)}`, { signal });
  }

  getToolStatus(name: string, signal?: AbortSignal): Promise<AdapterStatus> {
    return this.request<AdapterStatus>(
      `/tools/${encodeURIComponent(name)}/status`,
      { signal },
    );
  }

  getToolLogs(name: string, instance = 0, signal?: AbortSignal): Promise<unknown> {
    return this.request<unknown>(`/tools/${encodeURIComponent(name)}/logs`, {
      query: { instance },
      signal,
    });
  }

  createTool(payload: ToolData): Promise<ToolResource> {
    return this.request<ToolResource>("/tools", { method: "POST", body: payload });
  }

  updateTool(payload: ToolData): Promise<ToolResource> {
    return this.request<ToolResource>(
      `/tools/${encodeURIComponent(payload.name)}`,
      { method: "PUT", body: payload },
    );
  }

  deleteTool(name: string): Promise<void> {
    return this.request<void>(`/tools/${encodeURIComponent(name)}`, { method: "DELETE" });
  }

  // ---- MCP proxy ------------------------------------------------------

  /**
   * Issues a raw HTTP request against an adapter's MCP endpoint
   * (`POST /adapters/{name}/mcp`) or the tool router endpoint (`POST /mcp`)
   * when `name` is omitted. Returns the response so the caller can inspect
   * both the body and the headers (which carry `mcp-session-id`).
   */
  async sendMcpRequest(
    name: string | undefined,
    body: unknown,
    options?: { sessionId?: string; signal?: AbortSignal; accept?: string },
  ): Promise<Response> {
    const path = name ? `/adapters/${encodeURIComponent(name)}/mcp` : "/mcp";
    const headers = await this.buildHeaders({
      accept: options?.accept ?? "application/json, text/event-stream",
      contentType: "application/json",
    });
    if (options?.sessionId) {
      headers.set("mcp-session-id", options.sessionId);
    }
    return fetch(path, {
      method: "POST",
      headers,
      body: JSON.stringify(body),
      signal: options?.signal,
      credentials: "same-origin",
    });
  }

  // ---- Internals ------------------------------------------------------

  private async request<T>(path: string, options: RequestOptions = {}): Promise<T> {
    const url = options.query ? appendQuery(path, options.query) : path;
    const method = options.method ?? "GET";

    const headers = options.authenticated === false
      ? new Headers()
      : await this.buildHeaders({ accept: options.accept });

    if (options.body !== undefined) {
      headers.set("Content-Type", "application/json");
    }

    const response = await fetch(url, {
      method,
      headers,
      body: options.body === undefined ? undefined : JSON.stringify(options.body),
      credentials: "same-origin",
      signal: options.signal,
    });

    if (response.status === 204 || response.status === 205) {
      return undefined as T;
    }

    if (!response.ok) {
      const text = await response.text().catch(() => "");
      throw new ApiError(
        response.status,
        `${method} ${url} failed: ${response.status} ${response.statusText}`,
        text,
      );
    }

    const contentType = response.headers.get("content-type") ?? "";
    if (contentType.includes("application/json")) {
      return (await response.json()) as T;
    }
    // The /logs endpoints return JSON-ish strings; fall back to text + parse.
    const text = await response.text();
    if (!text) return undefined as T;
    try {
      return JSON.parse(text) as T;
    } catch {
      return text as unknown as T;
    }
  }

  private async buildHeaders(options: {
    accept?: string;
    contentType?: string;
  }): Promise<Headers> {
    const headers = new Headers();
    headers.set("Accept", options.accept ?? "application/json");
    if (options.contentType) headers.set("Content-Type", options.contentType);

    if (this.config.isDevelopment) {
      const identity = loadDevIdentity();
      for (const [key, value] of Object.entries(devIdentityHeaders(identity))) {
        headers.set(key, value as string);
      }
      return headers;
    }

    const token = await acquireToken(this.config);
    if (token) {
      headers.set("Authorization", `Bearer ${token}`);
    }
    return headers;
  }
}

function appendQuery(path: string, query: Record<string, string | number | undefined>): string {
  const search = new URLSearchParams();
  for (const [key, value] of Object.entries(query)) {
    if (value === undefined || value === null) continue;
    search.append(key, String(value));
  }
  const qs = search.toString();
  return qs ? `${path}?${qs}` : path;
}
