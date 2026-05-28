// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

import { type ReactNode } from "react";
import {
  Breadcrumb,
  BreadcrumbButton,
  BreadcrumbDivider,
  BreadcrumbItem,
  Caption1,
  makeStyles,
  tokens,
} from "@fluentui/react-components";
import { useNavigate } from "react-router-dom";

export interface BreadcrumbEntry {
  label: string;
  to?: string;
}

const useStyles = makeStyles({
  root: {
    display: "flex",
    flexDirection: "column",
    gap: "8px",
    marginBottom: "20px",
  },
  breadcrumb: {
    minHeight: "20px",
  },
  titleRow: {
    display: "flex",
    alignItems: "flex-start",
    justifyContent: "space-between",
    gap: "16px",
    flexWrap: "wrap",
  },
  titleBlock: {
    display: "flex",
    flexDirection: "column",
    gap: "4px",
    minWidth: 0,
    flex: "1 1 320px",
  },
  titleLine: {
    display: "flex",
    alignItems: "center",
    gap: "10px",
    flexWrap: "wrap",
    minWidth: 0,
  },
  title: {
    fontSize: tokens.fontSizeHero700,
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground1,
    lineHeight: tokens.lineHeightHero700,
    margin: 0,
    overflowWrap: "break-word",
  },
  description: {
    color: tokens.colorNeutralForeground3,
    maxWidth: "780px",
  },
  badges: {
    display: "flex",
    alignItems: "center",
    gap: "6px",
    flexWrap: "wrap",
  },
  commands: {
    display: "flex",
    alignItems: "center",
    gap: "8px",
    flexShrink: 0,
  },
});

export interface PageHeaderProps {
  title: ReactNode;
  description?: ReactNode;
  breadcrumbs?: BreadcrumbEntry[];
  badges?: ReactNode;
  commands?: ReactNode;
}

export function PageHeader({
  title,
  description,
  breadcrumbs,
  badges,
  commands,
}: PageHeaderProps) {
  const styles = useStyles();
  const navigate = useNavigate();

  return (
    <div className={styles.root}>
      {breadcrumbs && breadcrumbs.length > 0 && (
        <Breadcrumb
          aria-label="Breadcrumb"
          size="small"
          className={styles.breadcrumb}
        >
          {breadcrumbs.map((entry, i) => {
            const isLast = i === breadcrumbs.length - 1;
            return (
              <span key={`${entry.label}-${i}`} style={{ display: "contents" }}>
                <BreadcrumbItem>
                  <BreadcrumbButton
                    current={isLast}
                    onClick={
                      !isLast && entry.to ? () => navigate(entry.to!) : undefined
                    }
                  >
                    {entry.label}
                  </BreadcrumbButton>
                </BreadcrumbItem>
                {!isLast && <BreadcrumbDivider />}
              </span>
            );
          })}
        </Breadcrumb>
      )}
      <div className={styles.titleRow}>
        <div className={styles.titleBlock}>
          <div className={styles.titleLine}>
            <h1 className={styles.title}>{title}</h1>
            {badges && <div className={styles.badges}>{badges}</div>}
          </div>
          {description && (
            <Caption1 block className={styles.description}>
              {description}
            </Caption1>
          )}
        </div>
        {commands && <div className={styles.commands}>{commands}</div>}
      </div>
    </div>
  );
}
