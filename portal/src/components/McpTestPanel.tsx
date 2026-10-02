// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

import { useCallback, useEffect, useRef, useState } from "react";
import {
  Badge,
  Button,
  Caption1,
  Card,
  CardHeader,
  Dropdown,
  Field,
  Input,
  MessageBar,
  MessageBarBody,
  Option,
  Spinner,
  Subtitle2,
  Switch,
  Tab,
  TabList,
  Textarea,
  Tooltip,
  makeStyles,
  tokens,
} from "@fluentui/react-components";
import {
  ArrowClockwiseRegular,
  ChevronDown24Regular,
  ChevronRight24Regular,
  DismissRegular,
  PlayRegular,
  PlugConnected24Regular,
  PlugDisconnected24Regular,
  StopRegular,
} from "@fluentui/react-icons";
import { useGateway } from "../auth/PortalProvider";
import { formatApiError } from "../hooks/useAsync";
import { isRecord, MCP_VERSION, toolHeaders } from "../api/mcp";
import { readMcpBody } from "../api/mcpStream";

const useStyles = makeStyles({
  root: {
    display: "grid",
    gridTemplateColumns: "minmax(0, 1fr)",
    minWidth: 0,
    gap: "12px",
  },
  headerRow: {
    display: "flex",
    alignItems: "center",
    gap: "8px",
    flexWrap: "wrap",
  },
  endpoint: {
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
    overflowWrap: "anywhere",
  },
  serverInfo: {
    display: "flex",
    flexDirection: "column",
    gap: "2px",
    padding: "8px 12px",
    backgroundColor: tokens.colorNeutralBackground2,
    borderRadius: tokens.borderRadiusMedium,
  },
  toolGrid: {
    display: "grid",
    gridTemplateColumns: "repeat(auto-fill, minmax(220px, 1fr))",
    gap: "8px",
  },
  toolCard: {
    padding: "12px",
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    cursor: "pointer",
    backgroundColor: tokens.colorNeutralBackground1,
    textAlign: "left",
    transition: "background-color 80ms ease-in",
    ":hover": {
      backgroundColor: tokens.colorNeutralBackground1Hover,
    },
  },
  toolCardSelected: {
    border: `1px solid ${tokens.colorBrandStroke1}`,
    backgroundColor: tokens.colorBrandBackground2,
  },
  toolName: {
    fontWeight: tokens.fontWeightSemibold,
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase300,
  },
  toolDesc: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
    marginTop: "4px",
    display: "-webkit-box",
    WebkitLineClamp: 2,
    WebkitBoxOrient: "vertical",
    overflow: "hidden",
  },
  runCard: {
    padding: "12px",
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    display: "grid",
    gap: "10px",
  },
  pre: {
    margin: 0,
    padding: "12px",
    backgroundColor: tokens.colorNeutralBackground3,
    borderRadius: tokens.borderRadiusMedium,
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
    whiteSpace: "pre-wrap",
    wordBreak: "break-word",
    maxHeight: "320px",
    overflow: "auto",
  },
  advancedHeader: {
    display: "flex",
    alignItems: "center",
    gap: "4px",
    cursor: "pointer",
    color: tokens.colorNeutralForeground2,
    padding: "4px 0",
    userSelect: "none",
  },
  toolbar: {
    display: "flex",
    gap: "8px",
    flexWrap: "wrap",
    alignItems: "center",
  },
  sessionRow: {
    display: "flex",
    alignItems: "center",
    gap: "8px",
  },
});

interface Props {
  /**
   * Adapter / tool name to target. Omit for the dynamic tool-router endpoint
   * (`POST /mcp`).
   */
  resourceName?: string;
  /**
   * When set, the panel auto-selects this tool (instead of the first one
   * returned by `tools/list`) after connecting. Used by the tool detail page
   * so Run exercises the tool shown in the header rather than whichever tool
   * happens to come back first from the dynamic router.
   */
  pinnedTool?: string;
}

interface McpTool {
  name: string;
  title?: string;
  description?: string;
  inputSchema?: JsonSchema;
}

interface JsonSchema {
  type?: string;
  properties?: Record<string, JsonSchema>;
  required?: string[];
  description?: string;
  enum?: unknown[];
  default?: unknown;
  items?: JsonSchema;
}

interface ServerInfo {
  name?: string;
  version?: string;
  protocolVersion?: string;
}

interface ToolCallResult {
  isError: boolean;
  content: Array<{ type: string; text?: string; [key: string]: unknown }>;
  structuredContent?: unknown;
}

interface HistoryEntry {
  id: number;
  method: string;
  request: unknown;
  response: unknown;
  status: number;
  contentType: string | null;
  durationMs: number;
  error?: string;
}

let counter = 0;

/**
 * Friendly MCP test console. The default view connects to the server, lists
 * its tools, and lets the user run them by filling out a form derived from the
 * tool's JSON Schema. The full JSON-RPC sandbox is still available under the
 * Advanced section for power users.
 */
export function McpTestPanel({ resourceName, pinnedTool }: Props) {
  const styles = useStyles();
  const { api } = useGateway();

  // -- session / discovery state ---------------------------------------
  const [connected, setConnected] = useState(false);
  const activeRequests = useRef(new Set<AbortController>());
  const [pendingRequests, setPendingRequests] = useState(0);
  const [streamEvents, setStreamEvents] = useState<string[]>([]);
  const [serverInfo, setServerInfo] = useState<ServerInfo | undefined>();
  const [tools, setTools] = useState<McpTool[]>([]);
  const [connecting, setConnecting] = useState(false);
  const [connectError, setConnectError] = useState<string | undefined>();
  const [refreshing, setRefreshing] = useState(false);

  // -- per-tool run state ----------------------------------------------
  const [selectedTool, setSelectedTool] = useState<string | undefined>();
  const [toolArgs, setToolArgs] = useState<Record<string, unknown>>({});
  const [running, setRunning] = useState(false);
  const [result, setResult] = useState<ToolCallResult | undefined>();
  const [runError, setRunError] = useState<string | undefined>();

  // -- advanced (raw JSON-RPC) state -----------------------------------
  const [advancedOpen, setAdvancedOpen] = useState(false);
  const [rawText, setRawText] = useState<string>(
    JSON.stringify(rawSamples["server/discover"], null, 2),
  );
  const [rawHistory, setRawHistory] = useState<HistoryEntry[]>([]);
  const [rawSending, setRawSending] = useState(false);
  const [rawParseError, setRawParseError] = useState<string | undefined>();
  const [rawTransportError, setRawTransportError] = useState<string | undefined>();
  const [rawSelectedTab, setRawSelectedTab] = useState<"request" | "response">(
    "response",
  );

  const endpointPath = resourceName
    ? `POST /adapters/${resourceName}/mcp`
    : "POST /mcp";

  const sendRpc = useCallback(
    async (body: unknown) => {
      const controller = new AbortController();
      activeRequests.current.add(controller);
      setPendingRequests(activeRequests.current.size);
      try {
        const params = isRecord(body) && isRecord(body.params) ? body.params : {};
        const response = await api.sendMcpRequest(resourceName, body, {
          inputSchema: tools.find(tool => tool.name === params.name)?.inputSchema,
          signal: controller.signal,
        });
        const contentType = response.headers.get("content-type");
        const parsed = await readMcpBody(response, event => {
          setStreamEvents(events => [...events.slice(-19), prettyJson(event).slice(0, 16384)]);
        }, controller.signal);
        return { response, contentType, parsed };
      } finally {
        activeRequests.current.delete(controller);
        setPendingRequests(activeRequests.current.size);
      }
    },
    [api, resourceName, tools],
  );

  useEffect(() => {
    setConnected(false);
    setTools([]);
    setServerInfo(undefined);
    setRawHistory([]);
    setStreamEvents([]);
    setResult(undefined);
    const requests = activeRequests.current;
    return () => { requests.forEach(request => request.abort()); requests.clear(); };
  }, [api, resourceName]);

  const connect = async () => {
    setConnecting(true);
    setConnected(false);
    setConnectError(undefined);
    setStreamEvents([]);
    setResult(undefined);
    setRunError(undefined);
    try {
      const init = await sendRpc({ jsonrpc: "2.0", id: ++counter, method: "server/discover" });
      if (!init.response.ok) {
        extractRpcResult(init.parsed);
        throw new Error(
          `server/discover failed: HTTP ${init.response.status}. The server must support MCP ${MCP_VERSION}.`,
        );
      }
      const initResult = extractRpcResult(init.parsed) as
        | { supportedVersions?: string[]; _meta?: Record<string, { name?: string; version?: string }> }
        | undefined;
      if (!initResult?.supportedVersions?.includes(MCP_VERSION)) {
        throw new Error(`The server does not support MCP ${MCP_VERSION}. Upgrade the adapter or use the legacy gateway image.`);
      }
      const info = initResult._meta?.["io.modelcontextprotocol/serverInfo"];
      setServerInfo({
        name: info?.name,
        version: info?.version,
        protocolVersion: MCP_VERSION,
      });

      const list = await sendRpc({ jsonrpc: "2.0", id: ++counter, method: "tools/list" });
      if (!list.response.ok) {
        throw new Error(
          `tools/list failed: HTTP ${list.response.status} ${list.response.statusText}`,
        );
      }
      const listResult = extractRpcResult(list.parsed) as
        | { tools?: McpTool[] }
        | undefined;
      const fetched = validTools(listResult?.tools ?? []);
      setTools(fetched);
      setConnected(true);
      // Prefer the pinned tool (tool detail page) so Run targets the tool the
      // user is looking at; otherwise auto-select the first tool so the user
      // immediately sees a runnable form.
      const initial =
        (pinnedTool ? fetched.find((t) => t.name === pinnedTool) : undefined) ??
        fetched[0];
      if (initial) {
        setSelectedTool(initial.name);
        setToolArgs(defaultArgsFor(initial.inputSchema));
      } else {
        setSelectedTool(undefined);
        setToolArgs({});
      }
    } catch (err) {
      setConnectError(formatApiError(err));
    } finally {
      setConnecting(false);
    }
  };

  const refreshTools = async () => {
    if (!connected) return;
    setRefreshing(true);
    setRunError(undefined);
    try {
      const list = await sendRpc({
        jsonrpc: "2.0",
        id: ++counter,
        method: "tools/list",
      });
      if (!list.response.ok) {
        throw new Error(
          `tools/list failed: HTTP ${list.response.status} ${list.response.statusText}`,
        );
      }
      const listResult = extractRpcResult(list.parsed) as
        | { tools?: McpTool[] }
        | undefined;
      setTools(validTools(listResult?.tools ?? []));
    } catch (err) {
      setRunError(formatApiError(err));
    } finally {
      setRefreshing(false);
    }
  };

  const disconnect = () => {
    activeRequests.current.forEach(request => request.abort());
    setConnected(false);
    setServerInfo(undefined);
    setTools([]);
    setSelectedTool(undefined);
    setToolArgs({});
    setResult(undefined);
    setRunError(undefined);
    setConnectError(undefined);
    setStreamEvents([]);
  };

  const currentTool = tools.find((t) => t.name === selectedTool);

  const selectTool = (tool: McpTool) => {
    setSelectedTool(tool.name);
    setToolArgs(defaultArgsFor(tool.inputSchema));
    setResult(undefined);
    setRunError(undefined);
  };

  const runTool = async () => {
    if (!currentTool) return;
    setRunning(true);
    setStreamEvents([]);
    setRunError(undefined);
    setResult(undefined);
    try {
      const call = await sendRpc({
        jsonrpc: "2.0",
        id: ++counter,
        method: "tools/call",
        params: {
          name: currentTool.name,
          arguments: toolArgs,
        },
      });
      if (!call.response.ok) {
        extractRpcResult(call.parsed);
        throw new Error(
          `tools/call failed: HTTP ${call.response.status} ${call.response.statusText}`,
        );
      }
      const callResult = extractRpcResult(call.parsed) as ToolCallResult | undefined;
      if (!callResult) throw new Error("Server returned no result payload.");
      setResult(callResult);
    } catch (err) {
      setRunError(formatApiError(err));
    } finally {
      setRunning(false);
    }
  };

  // ----- advanced raw JSON-RPC ---------------------------------------
  const loadRawSample = (key: keyof typeof rawSamples) => {
    setRawText(JSON.stringify(rawSamples[key], null, 2));
    setRawParseError(undefined);
  };

  const sendRaw = async () => {
    setRawParseError(undefined);
    setRawTransportError(undefined);
    setStreamEvents([]);
    let parsed: unknown;
    try {
      parsed = JSON.parse(rawText);
    } catch (err) {
      setRawParseError((err as Error).message);
      return;
    }
    setRawSending(true);
    const started = performance.now();
    try {
      const { response, contentType, parsed: body } = await sendRpc(parsed);
      const elapsed = Math.round(performance.now() - started);
      const method = extractMethod(parsed) ?? "<unknown>";
      const entry: HistoryEntry = {
        id: ++counter,
        method,
        request: parsed,
        response: body,
        status: response.status,
        contentType,
        durationMs: elapsed,
        error: response.ok
          ? undefined
          : `HTTP ${response.status} ${response.statusText}`,
      };
      setRawHistory((h) => [entry, ...h].slice(0, 20));
      setRawSelectedTab("response");
    } catch (err) {
      setRawTransportError(formatApiError(err));
    } finally {
      setRawSending(false);
    }
  };

  const rawLatest = rawHistory[0];

  return (
    <Card className={styles.root}>
      <CardHeader
        header={<Subtitle2>Test connection</Subtitle2>}
        description={<span className={styles.endpoint}>{endpointPath}</span>}
        action={
          <div className={styles.sessionRow}>
            {connected ? (
              <>
                <Badge appearance="filled" color="success">connected</Badge>
                <Tooltip content="Disconnect" relationship="label">
                  <Button
                    appearance="subtle"
                    icon={<DismissRegular />}
                    onClick={disconnect}
                  />
                </Tooltip>
              </>
            ) : (
              <Badge appearance="outline" color="informative">
                disconnected
              </Badge>
            )}
          </div>
        }
      />

      {pendingRequests > 0 && (
        <div className={styles.toolbar}>
          <Spinner size="tiny" />
          <Button icon={<StopRegular />} onClick={() => activeRequests.current.forEach(request => request.abort())}>
            Cancel requests
          </Button>
        </div>
      )}
      {streamEvents.length > 0 && (
        <div aria-live="polite">
          <Caption1>Stream events</Caption1>
          <pre className={styles.pre}>{streamEvents.join("\n\n")}</pre>
        </div>
      )}

      {/* connect / server info */}
      {!connected ? (
        <div className={styles.toolbar}>
          <Button
            appearance="primary"
            icon={connecting ? <Spinner size="tiny" /> : <PlugConnected24Regular />}
            onClick={connect}
            disabled={connecting}
          >
            {connecting ? "Connecting…" : "Connect"}
          </Button>
        </div>
      ) : (
        <div className={styles.serverInfo}>
          <div>
            <strong>{serverInfo?.name ?? "(unnamed server)"}</strong>
            {serverInfo?.version ? ` · v${serverInfo.version}` : ""}
          </div>
          <Caption1>
            MCP {serverInfo?.protocolVersion ?? MCP_VERSION}
          </Caption1>
          <div className={styles.toolbar} style={{ marginTop: 4 }}>
            <Button
              size="small"
              appearance="secondary"
              icon={<ArrowClockwiseRegular />}
              onClick={refreshTools}
              disabled={refreshing}
            >
              {refreshing ? "Refreshing…" : "Refresh tools"}
            </Button>
            <Button
              size="small"
              appearance="subtle"
              icon={<PlugDisconnected24Regular />}
              onClick={disconnect}
            >
              Disconnect
            </Button>
          </div>
        </div>
      )}

      {connectError && (
        <MessageBar intent="error">
          <MessageBarBody>{connectError}</MessageBarBody>
        </MessageBar>
      )}

      {/* tool list */}
      {connected && (
        <div>
          <Caption1 block style={{ marginBottom: 6 }}>
            {tools.length === 0
              ? "Server reported zero tools."
              : `Tools (${tools.length})`}
          </Caption1>
          {tools.length > 0 && (
            <div className={styles.toolGrid}>
              {tools.map((t) => {
                const isSelected = t.name === selectedTool;
                return (
                  <button
                    key={t.name}
                    type="button"
                    className={`${styles.toolCard} ${
                      isSelected ? styles.toolCardSelected : ""
                    }`}
                    onClick={() => selectTool(t)}
                  >
                    <div className={styles.toolName}>{t.name}</div>
                    {(t.title || t.description) && (
                      <div className={styles.toolDesc}>
                        {t.title ?? t.description}
                      </div>
                    )}
                  </button>
                );
              })}
            </div>
          )}
        </div>
      )}

      {/* run form */}
      {connected && currentTool && (
        <div className={styles.runCard}>
          <div>
            <Subtitle2>{currentTool.title ?? currentTool.name}</Subtitle2>
            {currentTool.description && (
              <Caption1 block style={{ marginTop: 2 }}>
                {currentTool.description}
              </Caption1>
            )}
          </div>

          <SchemaForm
            schema={currentTool.inputSchema}
            value={toolArgs}
            onChange={setToolArgs}
          />

          <div className={styles.toolbar}>
            <Button
              appearance="primary"
              icon={running ? <Spinner size="tiny" /> : <PlayRegular />}
              onClick={runTool}
              disabled={running}
            >
              {running ? "Running…" : `Run ${currentTool.name}`}
            </Button>
          </div>

          {runError && (
            <MessageBar intent="error">
              <MessageBarBody>{runError}</MessageBarBody>
            </MessageBar>
          )}

          {result && <ToolResultView result={result} />}
        </div>
      )}

      {/* advanced raw console */}
      <div
        className={styles.advancedHeader}
        onClick={() => setAdvancedOpen((v) => !v)}
        role="button"
        tabIndex={0}
        onKeyDown={(e) => {
          if (e.key === "Enter" || e.key === " ") setAdvancedOpen((v) => !v);
        }}
      >
        {advancedOpen ? <ChevronDown24Regular /> : <ChevronRight24Regular />}
        <strong>Advanced</strong>
        <Caption1>Send raw JSON-RPC requests</Caption1>
      </div>

      {advancedOpen && (
        <div className={styles.runCard}>
          <div className={styles.toolbar}>
            {(Object.keys(rawSamples) as Array<keyof typeof rawSamples>).map(
              (key) => (
                <Button
                  key={key}
                  appearance="secondary"
                  size="small"
                  onClick={() => loadRawSample(key)}
                >
                  {key}
                </Button>
              ),
            )}
          </div>
          <Field
            label="JSON-RPC request"
            validationMessage={rawParseError}
            validationState={rawParseError ? "error" : "none"}
          >
            <Textarea
              rows={8}
              textarea={{
                style: {
                  fontFamily: tokens.fontFamilyMonospace,
                  fontSize: 13,
                },
              }}
              value={rawText}
              onChange={(_, d) => setRawText(d.value)}
            />
          </Field>
          <div className={styles.toolbar}>
            <Button
              appearance="primary"
              icon={<PlayRegular />}
              onClick={sendRaw}
              disabled={rawSending}
            >
              {rawSending ? "Sending…" : "Send"}
            </Button>
          </div>
          {rawTransportError && (
            <MessageBar intent="error">
              <MessageBarBody>{rawTransportError}</MessageBarBody>
            </MessageBar>
          )}
          {rawLatest && (
            <div>
              <TabList
                selectedValue={rawSelectedTab}
                onTabSelect={(_, d) =>
                  setRawSelectedTab(d.value as "request" | "response")
                }
              >
                <Tab value="response">
                  Response{" "}
                  <Badge
                    appearance="tint"
                    color={rawLatest.error ? "danger" : "success"}
                    style={{ marginLeft: 6 }}
                  >
                    {rawLatest.status} · {rawLatest.durationMs}ms
                  </Badge>
                </Tab>
                <Tab value="request">Request</Tab>
              </TabList>
              <pre className={styles.pre}>
                {rawSelectedTab === "request"
                  ? prettyJson(rawLatest.request)
                  : prettyJson(rawLatest.response)}
              </pre>
            </div>
          )}
        </div>
      )}
    </Card>
  );
}

// ---------- subcomponents ------------------------------------------------

interface SchemaFormProps {
  schema: JsonSchema | undefined;
  value: Record<string, unknown>;
  onChange: (next: Record<string, unknown>) => void;
}

/**
 * Renders a simple Fluent UI form from a JSON Schema object. Supports the
 * common MCP cases (string / number / integer / boolean / enum / array /
 * nested object). Anything we can't render natively falls back to a JSON
 * textarea so the user can still provide a value.
 */
function SchemaForm({ schema, value, onChange }: SchemaFormProps) {
  const properties = schema?.properties ?? {};
  const required = new Set(schema?.required ?? []);
  const entries = Object.entries(properties);

  if (entries.length === 0) {
    return <Caption1>This tool takes no input parameters.</Caption1>;
  }

  const set = (key: string, v: unknown) => onChange({ ...value, [key]: v });

  return (
    <div style={{ display: "grid", gap: 10 }}>
      {entries.map(([key, propSchema]) => (
        <SchemaField
          key={key}
          name={key}
          required={required.has(key)}
          schema={propSchema}
          value={value[key]}
          onChange={(v) => set(key, v)}
        />
      ))}
    </div>
  );
}

interface SchemaFieldProps {
  name: string;
  required: boolean;
  schema: JsonSchema;
  value: unknown;
  onChange: (next: unknown) => void;
}

function SchemaField({ name, required, schema, value, onChange }: SchemaFieldProps) {
  const label = (
    <span>
      <code>{name}</code>
      {schema.description ? ` — ${schema.description}` : ""}
    </span>
  );

  // enum → Dropdown
  if (Array.isArray(schema.enum) && schema.enum.length > 0) {
    const current = value === undefined ? "" : String(value);
    return (
      <Field label={label} required={required}>
        <Dropdown
          value={current}
          selectedOptions={current ? [current] : []}
          onOptionSelect={(_, d) => onChange(d.optionValue)}
        >
          {schema.enum.map((opt) => {
            const v = String(opt);
            return (
              <Option key={v} value={v}>
                {v}
              </Option>
            );
          })}
        </Dropdown>
      </Field>
    );
  }

  switch (schema.type) {
    case "boolean":
      return (
        <Field label={label} required={required}>
          <Switch
            checked={Boolean(value)}
            onChange={(_, d) => onChange(d.checked)}
          />
        </Field>
      );
    case "integer":
    case "number":
      return (
        <Field label={label} required={required}>
          <Input
            type="number"
            value={value === undefined || value === null ? "" : String(value)}
            onChange={(_, d) => {
              if (d.value === "") {
                onChange(undefined);
                return;
              }
              const parsed = schema.type === "integer"
                ? parseInt(d.value, 10)
                : parseFloat(d.value);
              onChange(Number.isNaN(parsed) ? d.value : parsed);
            }}
          />
        </Field>
      );
    case "object":
    case "array": {
      const text =
        value === undefined || value === null
          ? ""
          : typeof value === "string"
          ? value
          : prettyJson(value);
      return (
        <Field
          label={label}
          required={required}
          hint={`Enter a JSON ${schema.type}`}
        >
          <Textarea
            rows={4}
            textarea={{
              style: {
                fontFamily: tokens.fontFamilyMonospace,
                fontSize: 13,
              },
            }}
            value={text}
            onChange={(_, d) => {
              try {
                onChange(d.value === "" ? undefined : JSON.parse(d.value));
              } catch {
                // Keep the raw string so the user can keep typing; it will be
                // sent as-is and the server will reject malformed JSON.
                onChange(d.value);
              }
            }}
          />
        </Field>
      );
    }
    case "string":
    default:
      return (
        <Field label={label} required={required}>
          <Input
            value={value === undefined || value === null ? "" : String(value)}
            onChange={(_, d) => onChange(d.value)}
          />
        </Field>
      );
  }
}

function ToolResultView({ result }: { result: ToolCallResult }) {
  const styles = useStyles();
  return (
    <div style={{ display: "grid", gap: 8 }}>
      <div style={{ display: "flex", alignItems: "center", gap: 8 }}>
        <Subtitle2>Result</Subtitle2>
        <Badge
          appearance="tint"
          color={result.isError ? "danger" : "success"}
        >
          {result.isError ? "error" : "ok"}
        </Badge>
      </div>
      {result.content?.map((item, i) => {
        if (item.type === "text" && typeof item.text === "string") {
          return (
            <pre key={i} className={styles.pre}>
              {item.text}
            </pre>
          );
        }
        return (
          <pre key={i} className={styles.pre}>
            {prettyJson(item)}
          </pre>
        );
      })}
      {result.structuredContent !== undefined && (
        <>
          <Caption1>structuredContent</Caption1>
          <pre className={styles.pre}>{prettyJson(result.structuredContent)}</pre>
        </>
      )}
    </div>
  );
}

// ---------- helpers ------------------------------------------------------

const rawSamples: Record<string, object> = {
  "server/discover": { jsonrpc: "2.0", id: 1, method: "server/discover" },
  "tools/list": { jsonrpc: "2.0", id: 2, method: "tools/list" },
  "tools/call": {
    jsonrpc: "2.0",
    id: 3,
    method: "tools/call",
    params: { name: "<tool-name>", arguments: {} },
  },
  "subscriptions/listen": { jsonrpc: "2.0", id: 4, method: "subscriptions/listen", params: { notifications: { toolsListChanged: true } } },
};

function validTools(tools: McpTool[]): McpTool[] {
  return tools.filter(tool => {
    try { toolHeaders(tool.inputSchema, {}); return true; }
    catch { return false; }
  });
}

function defaultArgsFor(schema: JsonSchema | undefined): Record<string, unknown> {
  const out: Record<string, unknown> = {};
  const props = schema?.properties ?? {};
  for (const [name, prop] of Object.entries(props)) {
    if (prop.default !== undefined) out[name] = prop.default;
  }
  return out;
}

/**
 * Pulls the `.result` field out of whatever the MCP server returned. The
 * server may answer with a plain JSON object, an SSE stream (in which case
 * `readBody` returns an array of event payloads), or a raw string. We pick
 * the first entry that looks like a JSON-RPC response.
 */
function extractRpcResult(body: unknown): unknown {
  if (body === null || body === undefined) return undefined;
  if (Array.isArray(body)) {
    for (const evt of body) {
      const r = extractRpcResult(evt);
      if (r !== undefined) return r;
    }
    return undefined;
  }
  if (typeof body === "object") {
    const obj = body as { result?: unknown; error?: unknown };
    if (obj.error !== undefined) {
      const error = isRecord(obj.error) ? obj.error : {};
      throw new Error(`${error.message ?? "MCP request failed"} (${error.code ?? "unknown"})`);
    }
    if (obj.result !== undefined) {
      const result = isRecord(obj.result) ? obj.result : {};
      if (result.resultType !== "complete") {
        throw new Error(result.resultType === "input_required"
          ? "This call requires additional client input; the result is incomplete."
          : `Unsupported or missing resultType: ${String(result.resultType)}`);
      }
      return obj.result;
    }
  }
  return undefined;
}

function extractMethod(req: unknown): string | undefined {
  if (typeof req !== "object" || req === null) return undefined;
  const value = (req as { method?: unknown }).method;
  return typeof value === "string" ? value : undefined;
}

function prettyJson(value: unknown): string {
  try {
    return JSON.stringify(value, null, 2);
  } catch {
    return String(value);
  }
}
