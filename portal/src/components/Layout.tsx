// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

import { useState, type ReactNode } from "react";
import {
  Avatar,
  Button,
  Caption1,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  DialogTrigger,
  Field,
  Input,
  makeStyles,
  mergeClasses,
  tokens,
  Tooltip,
} from "@fluentui/react-components";
import {
  Link as RouterLink,
  NavLink,
  Outlet,
  useLocation,
} from "react-router-dom";
import {
  AppsListRegular,
  ChevronDoubleLeftRegular,
  ChevronDoubleRightRegular,
  PersonRegular,
  PuzzlePieceRegular,
  SignOutRegular,
} from "@fluentui/react-icons";
import { useGateway } from "../auth/PortalProvider";
import { signOut, getActiveAccount } from "../auth/msal";
import {
  defaultDevIdentity,
  loadDevIdentity,
  saveDevIdentity,
  type DevIdentity,
} from "../auth/devIdentity";

const HEADER_HEIGHT = "44px";
const RAIL_EXPANDED = "240px";
const RAIL_COLLAPSED = "52px";

const useStyles = makeStyles({
  root: {
    minHeight: "100vh",
    display: "grid",
    gridTemplateRows: `${HEADER_HEIGHT} 1fr`,
    backgroundColor: tokens.colorNeutralBackground2,
  },
  header: {
    gridRow: "1 / 2",
    display: "flex",
    alignItems: "center",
    padding: "0 16px",
    backgroundColor: tokens.colorNeutralBackground1,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
    gap: "12px",
    position: "sticky",
    top: 0,
    zIndex: 10,
  },
  headerBrand: {
    display: "flex",
    alignItems: "center",
    gap: "10px",
    color: tokens.colorNeutralForeground1,
    textDecoration: "none",
    paddingLeft: "4px",
  },
  brandMark: {
    width: "22px",
    height: "22px",
    display: "grid",
    gridTemplateColumns: "1fr 1fr",
    gridTemplateRows: "1fr 1fr",
    gap: "2px",
    flexShrink: 0,
  },
  brandMarkTile: {
    width: "100%",
    height: "100%",
  },
  brandWordmark: {
    fontSize: "15px",
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground1,
    letterSpacing: "0",
  },
  brandDivider: {
    width: "1px",
    height: "22px",
    backgroundColor: tokens.colorNeutralStroke1,
    margin: "0 10px",
  },
  brandProduct: {
    fontSize: tokens.fontSizeBase300,
    fontWeight: tokens.fontWeightRegular,
    color: tokens.colorNeutralForeground2,
  },
  headerSpacer: { flex: 1 },
  body: {
    gridRow: "2 / 3",
    display: "grid",
    gridTemplateColumns: `${RAIL_EXPANDED} 1fr`,
    minHeight: 0,
  },
  bodyCollapsed: {
    gridTemplateColumns: `${RAIL_COLLAPSED} 1fr`,
  },
  rail: {
    backgroundColor: tokens.colorNeutralBackground1,
    borderRight: `1px solid ${tokens.colorNeutralStroke2}`,
    padding: "12px 8px",
    display: "flex",
    flexDirection: "column",
    gap: "2px",
    position: "sticky",
    top: HEADER_HEIGHT,
    height: `calc(100vh - ${HEADER_HEIGHT})`,
    boxSizing: "border-box",
    overflowY: "auto",
  },
  railToggle: {
    alignSelf: "flex-end",
    marginBottom: "4px",
  },
  navItem: {
    position: "relative",
    display: "flex",
    alignItems: "center",
    gap: "12px",
    padding: "8px 12px",
    borderRadius: tokens.borderRadiusMedium,
    color: tokens.colorNeutralForeground2,
    textDecoration: "none",
    fontSize: tokens.fontSizeBase300,
    fontWeight: tokens.fontWeightRegular,
    transitionProperty: "background-color, color",
    transitionDuration: tokens.durationFaster,
    "&:hover": {
      backgroundColor: tokens.colorNeutralBackground1Hover,
      color: tokens.colorNeutralForeground1,
    },
    "&:focus-visible": {
      outline: `2px solid ${tokens.colorStrokeFocus2}`,
      outlineOffset: "-2px",
    },
  },
  navItemActive: {
    backgroundColor: tokens.colorNeutralBackground1Selected,
    color: tokens.colorNeutralForeground1,
    fontWeight: tokens.fontWeightSemibold,
    "&:hover": {
      backgroundColor: tokens.colorNeutralBackground1Hover,
      color: tokens.colorNeutralForeground1,
    },
    "&::before": {
      content: '""',
      position: "absolute",
      left: 0,
      top: "6px",
      bottom: "6px",
      width: "3px",
      borderRadius: "0 2px 2px 0",
      backgroundColor: tokens.colorBrandStroke1,
    },
  },
  navIcon: {
    fontSize: "20px",
    width: "20px",
    height: "20px",
    display: "grid",
    placeItems: "center",
    flexShrink: 0,
  },
  navLabel: {
    whiteSpace: "nowrap",
    overflow: "hidden",
    textOverflow: "ellipsis",
  },
  navLabelHidden: {
    display: "none",
  },
  navItemCollapsed: {
    justifyContent: "center",
    padding: "10px 0",
  },
  content: {
    minWidth: 0,
    padding: "24px 32px",
    maxWidth: "1440px",
    width: "100%",
    boxSizing: "border-box",
    margin: "0 auto",
  },
  identity: {
    display: "flex",
    alignItems: "center",
    gap: "10px",
  },
  identityName: {
    display: "flex",
    flexDirection: "column",
    alignItems: "flex-end",
    lineHeight: 1.1,
  },
  identityPrimary: {
    fontSize: tokens.fontSizeBase300,
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground1,
  },
  identityCaption: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
  },
});

interface NavEntry {
  to: string;
  label: string;
  icon: ReactNode;
  match: (pathname: string) => boolean;
}

const navEntries: NavEntry[] = [
  {
    to: "/adapters",
    label: "MCP Servers",
    icon: <AppsListRegular />,
    match: (p) => p.startsWith("/adapters") || p === "/",
  },
  {
    to: "/tools",
    label: "Tools",
    icon: <PuzzlePieceRegular />,
    match: (p) => p.startsWith("/tools"),
  },
];

export function Layout() {
  const styles = useStyles();
  const { config } = useGateway();
  const location = useLocation();
  const [collapsed, setCollapsed] = useState(false);

  return (
    <div className={styles.root}>
      <header className={styles.header}>
        <RouterLink className={styles.headerBrand} to="/">
          <span className={styles.brandMark} aria-hidden="true">
            <span
              className={styles.brandMarkTile}
              style={{ backgroundColor: "#F25022" }}
            />
            <span
              className={styles.brandMarkTile}
              style={{ backgroundColor: "#7FBA00" }}
            />
            <span
              className={styles.brandMarkTile}
              style={{ backgroundColor: "#00A4EF" }}
            />
            <span
              className={styles.brandMarkTile}
              style={{ backgroundColor: "#FFB900" }}
            />
          </span>
          <span className={styles.brandWordmark}>Microsoft</span>
          <span className={styles.brandDivider} />
          <span className={styles.brandProduct}>MCP Gateway</span>
        </RouterLink>
        <div className={styles.headerSpacer} />
        <div className={styles.identity}>
          {config.isDevelopment ? <DevIdentitySwitcher /> : <SignedInUser />}
        </div>
      </header>
      <div
        className={mergeClasses(
          styles.body,
          collapsed ? styles.bodyCollapsed : undefined,
        )}
      >
        <nav className={styles.rail} aria-label="Primary">
          <Tooltip
            content={collapsed ? "Expand navigation" : "Collapse navigation"}
            relationship="label"
          >
            <Button
              appearance="subtle"
              size="small"
              className={styles.railToggle}
              icon={
                collapsed ? (
                  <ChevronDoubleRightRegular />
                ) : (
                  <ChevronDoubleLeftRegular />
                )
              }
              onClick={() => setCollapsed((v) => !v)}
              aria-label={
                collapsed ? "Expand navigation" : "Collapse navigation"
              }
            />
          </Tooltip>
          {navEntries.map((entry) => {
            const active = entry.match(location.pathname);
            const item = (
              <NavLink
                key={entry.to}
                to={entry.to}
                className={mergeClasses(
                  styles.navItem,
                  active ? styles.navItemActive : undefined,
                  collapsed ? styles.navItemCollapsed : undefined,
                )}
                aria-current={active ? "page" : undefined}
              >
                <span className={styles.navIcon} aria-hidden="true">
                  {entry.icon}
                </span>
                <span
                  className={mergeClasses(
                    styles.navLabel,
                    collapsed ? styles.navLabelHidden : undefined,
                  )}
                >
                  {entry.label}
                </span>
              </NavLink>
            );
            return collapsed ? (
              <Tooltip
                key={entry.to}
                content={entry.label}
                relationship="label"
                positioning="after"
              >
                {item}
              </Tooltip>
            ) : (
              item
            );
          })}
        </nav>
        <main className={styles.content}>
          <Outlet />
        </main>
      </div>
    </div>
  );
}

function SignedInUser() {
  const styles = useStyles();
  const { config } = useGateway();
  const account = getActiveAccount();
  if (!account) return null;
  const name = account.name ?? account.username;
  return (
    <>
      <Avatar name={name} size={28} color="colorful" />
      <div className={styles.identityName}>
        <span className={styles.identityPrimary}>{name}</span>
        <span className={styles.identityCaption}>{account.username}</span>
      </div>
      <Tooltip content="Sign out" relationship="label">
        <Button
          appearance="subtle"
          icon={<SignOutRegular />}
          onClick={() => {
            void signOut(config);
          }}
          aria-label="Sign out"
        />
      </Tooltip>
    </>
  );
}

function DevIdentitySwitcher() {
  const styles = useStyles();
  const [identity, setIdentity] = useState<DevIdentity>(() => loadDevIdentity());
  const [open, setOpen] = useState(false);
  const [draft, setDraft] = useState<DevIdentity>(identity);

  const apply = () => {
    saveDevIdentity(draft);
    setIdentity(draft);
    setOpen(false);
    // Force any in-flight queries to re-run with the new headers.
    window.location.reload();
  };

  return (
    <Dialog open={open} onOpenChange={(_, data) => setOpen(data.open)}>
      <DialogTrigger disableButtonEnhancement>
        <Tooltip content="Switch dev identity" relationship="label">
          <Button
            appearance="subtle"
            icon={<PersonRegular />}
            onClick={() => setDraft(identity)}
          >
            <span className={styles.identityPrimary}>{identity.displayName}</span>
          </Button>
        </Tooltip>
      </DialogTrigger>
      <DialogSurface>
        <DialogBody>
          <DialogTitle>Development identity</DialogTitle>
          <DialogContent>
            <Caption1 block style={{ marginBottom: 12 }}>
              These values are sent to the gateway via the <code>X-Dev-UserId</code>,
              {" "}<code>X-Dev-Name</code>, and <code>X-Dev-Roles</code> headers so the
              local <code>DevelopmentAuthenticationHandler</code> mints a matching
              principal. Use roles like <code>mcp.admin</code>, <code>mcp.engineer</code>,
              or any value you configured for your adapters / tools.
            </Caption1>
            <Field label="User id" required>
              <Input
                value={draft.userId}
                onChange={(_, d) => setDraft((s) => ({ ...s, userId: d.value }))}
              />
            </Field>
            <Field label="Display name">
              <Input
                value={draft.displayName}
                onChange={(_, d) => setDraft((s) => ({ ...s, displayName: d.value }))}
              />
            </Field>
            <Field
              label="Roles (comma-separated)"
              hint="e.g. mcp.admin,mcp.engineer"
            >
              <Input
                value={draft.roles}
                onChange={(_, d) => setDraft((s) => ({ ...s, roles: d.value }))}
              />
            </Field>
          </DialogContent>
          <DialogActions>
            <Button appearance="secondary" onClick={() => setDraft(defaultDevIdentity)}>
              Reset
            </Button>
            <Button appearance="primary" onClick={apply}>
              Apply
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
