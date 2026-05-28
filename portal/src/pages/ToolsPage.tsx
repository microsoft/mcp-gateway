// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

import { useMemo, useState } from "react";
import {
  Button,
  Divider,
  Input,
  MessageBar,
  MessageBarBody,
  Spinner,
  Table,
  TableBody,
  TableCell,
  TableCellLayout,
  TableHeader,
  TableHeaderCell,
  TableRow,
  Toolbar,
  ToolbarButton,
  ToolbarDivider,
  makeStyles,
  tokens,
} from "@fluentui/react-components";
import {
  AddRegular,
  ArrowClockwiseRegular,
  PuzzlePieceRegular,
  SearchRegular,
} from "@fluentui/react-icons";
import { Link, useNavigate } from "react-router-dom";
import { useGateway } from "../auth/PortalProvider";
import { useAsync, formatApiError } from "../hooks/useAsync";
import { PageHeader } from "../components/PageHeader";

const useStyles = makeStyles({
  surface: {
    backgroundColor: tokens.colorNeutralBackground1,
    borderRadius: tokens.borderRadiusLarge,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    boxShadow: tokens.shadow2,
    overflow: "hidden",
  },
  commandBar: {
    display: "flex",
    alignItems: "center",
    justifyContent: "space-between",
    gap: "12px",
    padding: "8px 12px",
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground1,
  },
  commandBarLeft: {
    display: "flex",
    alignItems: "center",
    gap: "4px",
    flexWrap: "wrap",
  },
  search: {
    width: "320px",
    maxWidth: "40vw",
  },
  count: {
    padding: "10px 16px",
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
    backgroundColor: tokens.colorNeutralBackground2,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  empty: {
    padding: "48px 24px",
    display: "flex",
    flexDirection: "column",
    alignItems: "center",
    gap: "12px",
    color: tokens.colorNeutralForeground3,
    textAlign: "center",
  },
  emptyIcon: {
    fontSize: "32px",
    color: tokens.colorNeutralForeground4,
  },
  loading: {
    padding: "48px",
    display: "flex",
    justifyContent: "center",
  },
  table: {
    "& tbody tr": {
      cursor: "pointer",
    },
    "& tbody tr:hover": {
      backgroundColor: tokens.colorNeutralBackground1Hover,
    },
  },
  nameCell: {
    fontWeight: tokens.fontWeightSemibold,
  },
  mono: {
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground2,
  },
  description: {
    color: tokens.colorNeutralForeground2,
    overflow: "hidden",
    textOverflow: "ellipsis",
    whiteSpace: "nowrap",
    maxWidth: "320px",
    display: "block",
  },
});

export function ToolsPage() {
  const styles = useStyles();
  const { api } = useGateway();
  const navigate = useNavigate();
  const [filter, setFilter] = useState("");
  const list = useAsync((signal) => api.listTools(signal), [api]);

  const filtered = useMemo(() => {
    if (!list.data) return [];
    const q = filter.trim().toLowerCase();
    if (!q) return list.data;
    return list.data.filter((t) =>
      [
        t.name,
        t.imageName,
        t.description,
        t.createdBy,
        t.toolDefinition?.tool?.description,
      ]
        .filter(Boolean)
        .some((v) => (v as string).toLowerCase().includes(q)),
    );
  }, [list.data, filter]);

  return (
    <div>
      <PageHeader
        breadcrumbs={[{ label: "Home", to: "/" }, { label: "Tools" }]}
        title="Tools"
        description="Tools are deployments that expose a single MCP tool definition and are routed dynamically by the tool gateway router."
      />

      {list.error && (
        <MessageBar intent="error" style={{ marginBottom: 16 }}>
          <MessageBarBody>{formatApiError(list.error)}</MessageBarBody>
        </MessageBar>
      )}

      <div className={styles.surface}>
        <div className={styles.commandBar}>
          <Toolbar className={styles.commandBarLeft} size="small">
            <ToolbarButton
              appearance="primary"
              icon={<AddRegular />}
              onClick={() => navigate("/tools/new")}
            >
              New tool
            </ToolbarButton>
            <ToolbarDivider />
            <ToolbarButton
              icon={<ArrowClockwiseRegular />}
              onClick={list.reload}
              disabled={list.loading}
            >
              Refresh
            </ToolbarButton>
          </Toolbar>
          <Input
            className={styles.search}
            contentBefore={<SearchRegular />}
            placeholder="Filter by name, image, owner…"
            value={filter}
            onChange={(_, d) => setFilter(d.value)}
            size="small"
          />
        </div>

        <div className={styles.count}>
          {list.loading ? (
            "Loading…"
          ) : (
            <>
              <strong>{filtered.length}</strong> tool
              {filtered.length === 1 ? "" : "s"}
              {filter && list.data && filter.trim().length > 0 ? (
                <> of {list.data.length}</>
              ) : null}
            </>
          )}
        </div>

        {list.loading ? (
          <div className={styles.loading}>
            <Spinner label="Loading tools…" />
          </div>
        ) : filtered.length === 0 ? (
          <div className={styles.empty}>
            <PuzzlePieceRegular className={styles.emptyIcon} />
            <div>
              {list.data && list.data.length === 0
                ? "No tools yet."
                : "No tools match your filter."}
            </div>
            {list.data && list.data.length === 0 && (
              <Button
                appearance="primary"
                icon={<AddRegular />}
                onClick={() => navigate("/tools/new")}
              >
                Register your first tool
              </Button>
            )}
          </div>
        ) : (
          <Table size="small" aria-label="Tool list" className={styles.table}>
            <TableHeader>
              <TableRow>
                <TableHeaderCell>Name</TableHeaderCell>
                <TableHeaderCell>Description</TableHeaderCell>
                <TableHeaderCell>Image</TableHeaderCell>
                <TableHeaderCell>Endpoint</TableHeaderCell>
                <TableHeaderCell>Owner</TableHeaderCell>
                <TableHeaderCell>Updated</TableHeaderCell>
              </TableRow>
            </TableHeader>
            <TableBody>
              {filtered.map((t) => (
                <TableRow
                  key={t.id}
                  onClick={() => navigate(`/tools/${encodeURIComponent(t.name)}`)}
                >
                  <TableCell>
                    <TableCellLayout>
                      <Link
                        to={`/tools/${encodeURIComponent(t.name)}`}
                        className={styles.nameCell}
                        onClick={(e) => e.stopPropagation()}
                      >
                        {t.name}
                      </Link>
                    </TableCellLayout>
                  </TableCell>
                  <TableCell>
                    <span
                      className={styles.description}
                      title={
                        t.toolDefinition?.tool?.description ??
                        t.description ??
                        undefined
                      }
                    >
                      {t.toolDefinition?.tool?.description ??
                        t.description ??
                        "—"}
                    </span>
                  </TableCell>
                  <TableCell>
                    <span className={styles.mono}>
                      {t.imageName}:{t.imageVersion}
                    </span>
                  </TableCell>
                  <TableCell>
                    <span className={styles.mono}>
                      :{t.toolDefinition?.port}
                      {t.toolDefinition?.path}
                    </span>
                  </TableCell>
                  <TableCell>{t.createdBy}</TableCell>
                  <TableCell>
                    {new Date(t.lastUpdatedAt).toLocaleString()}
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
      </div>
      <Divider style={{ visibility: "hidden", marginTop: 16 }} />
    </div>
  );
}
