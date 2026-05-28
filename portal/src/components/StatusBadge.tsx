// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

import { Badge } from "@fluentui/react-components";
import type { AdapterStatus } from "../api/types";

interface Props {
  status: AdapterStatus | undefined;
  loading?: boolean;
}

/**
 * Render the rollout state of a deployment as a Fluent UI badge. The gateway
 * exposes a free-form `replicaStatus` string plus replica counters; we map
 * that to a coarse severity so the list view stays scannable.
 */
export function StatusBadge({ status, loading }: Props) {
  if (loading) return <Badge appearance="outline">…</Badge>;
  if (!status) return <Badge appearance="outline" color="informative">Unknown</Badge>;

  const label = describe(status);
  const color = colorFor(status);
  return <Badge appearance="filled" color={color}>{label}</Badge>;
}

function describe(s: AdapterStatus): string {
  const ready = s.readyReplicas ?? 0;
  const available = s.availableReplicas ?? 0;
  const updated = s.updatedReplicas ?? 0;
  if (s.replicaStatus && s.replicaStatus.toLowerCase() !== "ready") {
    return `${s.replicaStatus} (${ready}/${updated})`;
  }
  return `Ready ${ready}/${Math.max(available, updated, ready)}`;
}

function colorFor(s: AdapterStatus): "success" | "warning" | "danger" | "informative" {
  const ready = s.readyReplicas ?? 0;
  const desired = Math.max(s.updatedReplicas ?? 0, s.availableReplicas ?? 0, ready);
  if (desired === 0) return "informative";
  if (ready >= desired) return "success";
  if (ready === 0) return "danger";
  return "warning";
}
