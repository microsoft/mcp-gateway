// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

import { useMemo, useState } from "react";
import { useNavigate, useParams } from "react-router-dom";
import {
  Badge,
  Body1,
  Button,
  Caption1,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  DialogTrigger,
  MessageBar,
  MessageBarBody,
  SpinButton,
  Spinner,
  Subtitle2,
  Tab,
  TabList,
  makeStyles,
  tokens,
} from "@fluentui/react-components";
import {
  ArrowClockwiseRegular,
  DeleteRegular,
  EditRegular,
} from "@fluentui/react-icons";
import { useGateway } from "../auth/PortalProvider";
import { useAsync, formatApiError } from "../hooks/useAsync";
import { ToolForm } from "../components/ToolForm";
import { StatusBadge } from "../components/StatusBadge";
import { McpTestPanel } from "../components/McpTestPanel";
import { PageHeader } from "../components/PageHeader";

const useStyles = makeStyles({
  page: {
    display: "flex",
    flexDirection: "column",
    gap: "16px",
  },
  tabBar: {
    backgroundColor: tokens.colorNeutralBackground1,
    borderRadius: tokens.borderRadiusLarge,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    boxShadow: tokens.shadow2,
    padding: "0 8px",
  },
  card: {
    backgroundColor: tokens.colorNeutralBackground1,
    borderRadius: tokens.borderRadiusLarge,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    boxShadow: tokens.shadow2,
    overflow: "hidden",
  },
  cardHeader: {
    display: "flex",
    alignItems: "center",
    justifyContent: "space-between",
    padding: "12px 16px",
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  cardBody: {
    padding: "16px",
  },
  meta: {
    display: "grid",
    gridTemplateColumns: "repeat(auto-fill, minmax(220px, 1fr))",
    gap: "16px",
  },
  metaCell: {
    display: "flex",
    flexDirection: "column",
    gap: "4px",
  },
  metaCaption: {
    color: tokens.colorNeutralForeground3,
    textTransform: "uppercase",
    letterSpacing: "0.4px",
    fontSize: "11px",
    fontWeight: tokens.fontWeightSemibold,
  },
  metaValue: {
    color: tokens.colorNeutralForeground1,
    fontSize: tokens.fontSizeBase300,
    wordBreak: "break-word",
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
    maxHeight: "480px",
    overflow: "auto",
    color: tokens.colorNeutralForeground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  logsBar: {
    display: "flex",
    alignItems: "end",
    gap: "12px",
    padding: "12px 16px",
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground2,
  },
  badgeStrip: {
    display: "flex",
    alignItems: "center",
    gap: "6px",
    flexWrap: "wrap",
  },
  mono: {
    fontFamily: tokens.fontFamilyMonospace,
  },
});

export function ToolDetailPage() {
  const styles = useStyles();
  const { name } = useParams<{ name: string }>();
  const navigate = useNavigate();
  const { api } = useGateway();
  const [tab, setTab] = useState<"overview" | "logs" | "test" | "edit">(
    "overview",
  );
  const [logInstance, setLogInstance] = useState(0);
  const [deleting, setDeleting] = useState(false);
  const [deleteError, setDeleteError] = useState<string | undefined>();
  const [editError, setEditError] = useState<string | undefined>();

  const tool = useAsync((signal) => api.getTool(name!, signal), [api, name]);
  const status = useAsync(
    (signal) => api.getToolStatus(name!, signal),
    [api, name],
  );
  const logs = useAsync(
    (signal) => api.getToolLogs(name!, logInstance, signal),
    [api, name, logInstance, tab === "logs"],
  );
  const formattedLogs = useMemo(() => formatLogs(logs.data), [logs.data]);

  const refresh = () => {
    tool.reload();
    status.reload();
    if (tab === "logs") logs.reload();
  };

  const handleDelete = async () => {
    setDeleting(true);
    setDeleteError(undefined);
    try {
      await api.deleteTool(name!);
      navigate("/tools");
    } catch (err) {
      setDeleteError(formatApiError(err));
      setDeleting(false);
    }
  };

  if (tool.loading) return <Spinner label="Loading tool…" />;
  if (tool.error || !tool.data) {
    return (
      <MessageBar intent="error">
        <MessageBarBody>{formatApiError(tool.error) || "Tool not found."}</MessageBarBody>
      </MessageBar>
    );
  }

  const t = tool.data;

  return (
    <div className={styles.page}>
      <PageHeader
        breadcrumbs={[
          { label: "Home", to: "/" },
          { label: "Tools", to: "/tools" },
          { label: t.name },
        ]}
        title={t.name}
        description={
          t.toolDefinition?.tool?.description ??
          t.description ??
          "No description"
        }
        badges={
          <div className={styles.badgeStrip}>
            <StatusBadge status={status.data} loading={status.loading} />
            <Badge appearance="tint" className={styles.mono}>
              {t.imageName}:{t.imageVersion}
            </Badge>
            <Badge appearance="tint" color="brand" className={styles.mono}>
              :{t.toolDefinition?.port}
              {t.toolDefinition?.path}
            </Badge>
          </div>
        }
        commands={
          <>
            <Button
              appearance="subtle"
              icon={<ArrowClockwiseRegular />}
              onClick={refresh}
            >
              Refresh
            </Button>
            <Button
              appearance="secondary"
              icon={<EditRegular />}
              onClick={() => setTab("edit")}
            >
              Edit
            </Button>
            <Dialog>
              <DialogTrigger disableButtonEnhancement>
                <Button
                  appearance="subtle"
                  icon={<DeleteRegular />}
                  aria-label="Delete tool"
                >
                  Delete
                </Button>
              </DialogTrigger>
              <DialogSurface>
                <DialogBody>
                  <DialogTitle>Delete tool "{t.name}"?</DialogTitle>
                  <DialogContent>
                    This permanently removes the tool deployment and its
                    definition.
                    {deleteError && (
                      <MessageBar intent="error" style={{ marginTop: 8 }}>
                        <MessageBarBody>{deleteError}</MessageBarBody>
                      </MessageBar>
                    )}
                  </DialogContent>
                  <DialogActions>
                    <DialogTrigger disableButtonEnhancement>
                      <Button appearance="secondary" disabled={deleting}>
                        Cancel
                      </Button>
                    </DialogTrigger>
                    <Button
                      appearance="primary"
                      onClick={handleDelete}
                      disabled={deleting}
                    >
                      {deleting ? "Deleting…" : "Delete"}
                    </Button>
                  </DialogActions>
                </DialogBody>
              </DialogSurface>
            </Dialog>
          </>
        }
      />

      <div className={styles.tabBar}>
        <TabList
          selectedValue={tab}
          onTabSelect={(_, d) => setTab(d.value as typeof tab)}
        >
          <Tab value="overview">Overview</Tab>
          <Tab value="test">Test connection</Tab>
          <Tab value="logs">Logs</Tab>
          <Tab value="edit">Edit</Tab>
        </TabList>
      </div>

      {tab === "overview" && (
        <>
          <div className={styles.card}>
            <div className={styles.cardHeader}>
              <Subtitle2>Tool definition</Subtitle2>
            </div>
            <div className={styles.cardBody}>
              <pre className={styles.pre}>
                {JSON.stringify(t.toolDefinition, null, 2)}
              </pre>
            </div>
          </div>
          <div className={styles.card}>
            <div className={styles.cardHeader}>
              <Subtitle2>Deployment</Subtitle2>
            </div>
            <div className={styles.cardBody}>
              <div className={styles.meta}>
                <MetaCell label="Created by" value={t.createdBy} />
                <MetaCell
                  label="Created at"
                  value={new Date(t.createdAt).toLocaleString()}
                />
                <MetaCell
                  label="Updated at"
                  value={new Date(t.lastUpdatedAt).toLocaleString()}
                />
                <MetaCell label="Replicas" value={String(t.replicaCount)} />
                <MetaCell
                  label="Workload identity"
                  value={t.useWorkloadIdentity ? "Enabled" : "Disabled"}
                />
                <MetaCell
                  label="Required roles"
                  value={
                    t.requiredRoles.length ? t.requiredRoles.join(", ") : "—"
                  }
                />
              </div>
            </div>
          </div>
        </>
      )}

      {tab === "test" && (
        <>
          <MessageBar intent="info">
            <MessageBarBody>
              This panel sends requests to <code>POST /mcp</code> (the tool
              router). Use the <code>tools/call</code> sample with{" "}
              <code>name: "{t.name}"</code> to exercise this tool end-to-end.
            </MessageBarBody>
          </MessageBar>
          <McpTestPanel />
        </>
      )}

      {tab === "logs" && (
        <div className={styles.card}>
          <div className={styles.cardHeader}>
            <Subtitle2>Pod logs</Subtitle2>
          </div>
          <div className={styles.logsBar}>
            <div style={{ display: "flex", flexDirection: "column", gap: 4 }}>
              <Caption1>Instance</Caption1>
              <SpinButton
                min={0}
                max={32}
                value={logInstance}
                onChange={(_, d) => setLogInstance(d.value ?? 0)}
              />
            </div>
            <Button
              icon={<ArrowClockwiseRegular />}
              onClick={logs.reload}
              disabled={logs.loading}
            >
              Reload
            </Button>
            {logs.loading && <Spinner size="tiny" />}
          </div>
          <div className={styles.cardBody}>
            {logs.error && (
              <MessageBar intent="error" style={{ marginBottom: 12 }}>
                <MessageBarBody>{formatApiError(logs.error)}</MessageBarBody>
              </MessageBar>
            )}
            <pre className={styles.pre}>{formattedLogs || "(empty)"}</pre>
          </div>
        </div>
      )}

      {tab === "edit" && (
        <div className={styles.card}>
          <div className={styles.cardHeader}>
            <Subtitle2>Edit tool</Subtitle2>
          </div>
          <div className={styles.cardBody}>
            {editError && (
              <MessageBar intent="error" style={{ marginBottom: 12 }}>
                <MessageBarBody>{editError}</MessageBarBody>
              </MessageBar>
            )}
            <ToolForm
              initial={t}
              disableName
              submitLabel="Save changes"
              onSubmit={async (values) => {
                setEditError(undefined);
                try {
                  await api.updateTool({ ...values, name: t.name });
                  tool.reload();
                  status.reload();
                  setTab("overview");
                } catch (err) {
                  setEditError(formatApiError(err));
                  throw err;
                }
              }}
              onCancel={() => setTab("overview")}
            />
          </div>
        </div>
      )}
    </div>
  );
}

function MetaCell({ label, value }: { label: string; value: string }) {
  const styles = useStyles();
  return (
    <div className={styles.metaCell}>
      <span className={styles.metaCaption}>{label}</span>
      <Body1 className={styles.metaValue}>{value}</Body1>
    </div>
  );
}

function formatLogs(value: unknown): string {
  if (value === undefined || value === null) return "";
  if (typeof value === "string") return value;
  try {
    return JSON.stringify(value, null, 2);
  } catch {
    return String(value);
  }
}
