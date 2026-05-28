// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

import {
  Button,
  Caption1,
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
  AppsListDetailRegular,
  SearchRegular,
} from "@fluentui/react-icons";
import { Link, useNavigate } from "react-router-dom";
import { useMemo, useState } from "react";
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
});

export function AdaptersPage() {
  const styles = useStyles();
  const { api } = useGateway();
  const navigate = useNavigate();
  const [filter, setFilter] = useState("");
  const list = useAsync((signal) => api.listAdapters(signal), [api]);

  const filtered = useMemo(() => {
    if (!list.data) return [];
    const q = filter.trim().toLowerCase();
    if (!q) return list.data;
    return list.data.filter((a) =>
      [a.name, a.imageName, a.description, a.createdBy]
        .filter(Boolean)
        .some((v) => v.toLowerCase().includes(q)),
    );
  }, [list.data, filter]);

  return (
    <div>
      <PageHeader
        breadcrumbs={[{ label: "Home", to: "/" }, { label: "MCP Servers" }]}
        title="MCP Servers"
        description="Manage the lifecycle of MCP servers (adapters) hosted by this gateway."
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
              onClick={() => navigate("/adapters/new")}
            >
              New adapter
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
              <strong>{filtered.length}</strong> adapter
              {filtered.length === 1 ? "" : "s"}
              {filter && list.data && filter.trim().length > 0 ? (
                <> of {list.data.length}</>
              ) : null}
            </>
          )}
        </div>

        {list.loading ? (
          <div className={styles.loading}>
            <Spinner label="Loading adapters…" />
          </div>
        ) : filtered.length === 0 ? (
          <div className={styles.empty}>
            <AppsListDetailRegular className={styles.emptyIcon} />
            <div>
              {list.data && list.data.length === 0
                ? "No adapters yet."
                : "No adapters match your filter."}
            </div>
            {list.data && list.data.length === 0 && (
              <Button
                appearance="primary"
                icon={<AddRegular />}
                onClick={() => navigate("/adapters/new")}
              >
                Create your first adapter
              </Button>
            )}
          </div>
        ) : (
          <Table size="small" aria-label="Adapter list" className={styles.table}>
            <TableHeader>
              <TableRow>
                <TableHeaderCell>Name</TableHeaderCell>
                <TableHeaderCell>Image</TableHeaderCell>
                <TableHeaderCell>Replicas</TableHeaderCell>
                <TableHeaderCell>Owner</TableHeaderCell>
                <TableHeaderCell>Updated</TableHeaderCell>
                <TableHeaderCell>Required roles</TableHeaderCell>
              </TableRow>
            </TableHeader>
            <TableBody>
              {filtered.map((a) => (
                <TableRow
                  key={a.id}
                  onClick={() =>
                    navigate(`/adapters/${encodeURIComponent(a.name)}`)
                  }
                >
                  <TableCell>
                    <TableCellLayout>
                      <Link
                        to={`/adapters/${encodeURIComponent(a.name)}`}
                        className={styles.nameCell}
                        onClick={(e) => e.stopPropagation()}
                      >
                        {a.name}
                      </Link>
                    </TableCellLayout>
                  </TableCell>
                  <TableCell>
                    <span className={styles.mono}>
                      {a.imageName}:{a.imageVersion}
                    </span>
                  </TableCell>
                  <TableCell>{a.replicaCount}</TableCell>
                  <TableCell>{a.createdBy}</TableCell>
                  <TableCell>
                    {new Date(a.lastUpdatedAt).toLocaleString()}
                  </TableCell>
                  <TableCell>
                    {a.requiredRoles.length === 0 ? (
                      <Caption1>—</Caption1>
                    ) : (
                      <span className={styles.mono}>
                        {a.requiredRoles.join(", ")}
                      </span>
                    )}
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
