// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

import { useState } from "react";
import {
  Button,
  Caption1,
  Field,
  Input,
  Switch,
  Tag,
  TagGroup,
  Textarea,
  makeStyles,
  tokens,
  SpinButton,
  MessageBar,
  MessageBarBody,
} from "@fluentui/react-components";
import {
  AddRegular,
  ShieldKeyholeRegular,
} from "@fluentui/react-icons";
import type { AdapterData } from "../api/types";
import { formatApiError } from "../hooks/useAsync";

const useStyles = makeStyles({
  form: {
    display: "grid",
    gap: "16px",
    maxWidth: "720px",
  },
  row: {
    display: "grid",
    gap: "16px",
    gridTemplateColumns: "2fr 1fr",
  },
  envEditor: {
    display: "grid",
    gap: "8px",
  },
  envRow: {
    display: "grid",
    gridTemplateColumns: "1fr 1fr auto",
    gap: "8px",
    alignItems: "end",
  },
  rolesEditor: {
    display: "flex",
    flexDirection: "column",
    gap: "8px",
  },
  rolesInputRow: {
    display: "grid",
    gridTemplateColumns: "1fr auto",
    gap: "8px",
    alignItems: "end",
  },
  rolesEmpty: {
    color: tokens.colorNeutralForeground3,
    fontStyle: "italic",
  },
  helper: {
    color: tokens.colorNeutralForeground3,
  },
  actions: {
    display: "flex",
    gap: "12px",
    marginTop: "8px",
  },
});

/**
 * The set of fields shared by adapters and tools. The tool form layers a
 * separate `ToolDefinition` editor on top of this component (see ToolForm).
 */
export interface AdapterFormValues extends AdapterData {}

interface Props {
  initial?: Partial<AdapterFormValues>;
  /** When true, the `name` field is read-only (we never rename resources). */
  disableName?: boolean;
  submitLabel: string;
  onSubmit: (values: AdapterFormValues) => Promise<void>;
  onCancel?: () => void;
  /** Extra slot rendered between the metadata fields and the submit row. */
  children?: React.ReactNode;
}

const blank: AdapterFormValues = {
  name: "",
  imageName: "",
  imageVersion: "latest",
  environmentVariables: {},
  replicaCount: 1,
  description: "",
  useWorkloadIdentity: false,
  requiredRoles: [],
};

export function AdapterFormFields({
  initial,
  disableName,
  submitLabel,
  onSubmit,
  onCancel,
  children,
}: Props) {
  const styles = useStyles();
  const [values, setValues] = useState<AdapterFormValues>(() => ({
    ...blank,
    ...initial,
    environmentVariables: { ...(initial?.environmentVariables ?? {}) },
    requiredRoles: [...(initial?.requiredRoles ?? [])],
  }));
  // The env-var editor is row-oriented (ordered, duplicates / empties allowed
  // while the user is typing). We keep it in its own state and only collapse
  // it back into a dictionary at submit time so half-typed rows don't get
  // dropped or merged.
  const [envRows, setEnvRows] = useState<Array<{ key: string; value: string }>>(
    () =>
      Object.entries(initial?.environmentVariables ?? {}).map(([key, value]) => ({
        key,
        value,
      })),
  );
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | undefined>();
  // The role picker uses a small input + Add button and renders existing roles
  // as dismissible chips. We keep the in-progress role text in its own state
  // so submit can flush a pending value if the user forgot to press Add.
  const [roleDraft, setRoleDraft] = useState("");

  const setEnvRowAt = (idx: number, patch: Partial<{ key: string; value: string }>) => {
    setEnvRows((rows) => rows.map((row, i) => (i === idx ? { ...row, ...patch } : row)));
  };

  const addRole = (raw: string) => {
    const trimmed = raw.trim();
    if (!trimmed) return;
    setValues((s) =>
      s.requiredRoles.includes(trimmed)
        ? s
        : { ...s, requiredRoles: [...s.requiredRoles, trimmed] },
    );
    setRoleDraft("");
  };

  const removeRole = (role: string) => {
    setValues((s) => ({
      ...s,
      requiredRoles: s.requiredRoles.filter((r) => r !== role),
    }));
  };

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    setSubmitting(true);
    setError(undefined);
    try {
      // Collapse the env editor rows into a dictionary at submit, dropping
      // rows whose key is blank and trimming whitespace. Last write wins on
      // duplicate keys, matching how Kubernetes treats env entries.
      const environmentVariables: Record<string, string> = {};
      for (const { key, value } of envRows) {
        const trimmed = key.trim();
        if (!trimmed) continue;
        environmentVariables[trimmed] = value;
      }
      // Flush a pending role draft so users don't lose what's in the input.
      const pendingRole = roleDraft.trim();
      const finalRoles = [...values.requiredRoles.map((r) => r.trim()).filter(Boolean)];
      if (pendingRole && !finalRoles.includes(pendingRole)) {
        finalRoles.push(pendingRole);
      }
      await onSubmit({
        ...values,
        environmentVariables,
        requiredRoles: finalRoles,
      });
    } catch (err) {
      setError(formatApiError(err));
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <form className={styles.form} onSubmit={handleSubmit}>
      {error && (
        <MessageBar intent="error">
          <MessageBarBody>{error}</MessageBarBody>
        </MessageBar>
      )}

      <div className={styles.row}>
        <Field label="Name" required hint="lowercase letters, numbers and dashes only">
          <Input
            value={values.name}
            disabled={disableName}
            onChange={(_, d) => setValues((s) => ({ ...s, name: d.value }))}
          />
        </Field>
        <Field label="Replicas" required>
          <SpinButton
            min={0}
            max={32}
            value={values.replicaCount}
            onChange={(_, d) =>
              setValues((s) => ({ ...s, replicaCount: d.value ?? s.replicaCount }))
            }
          />
        </Field>
      </div>

      <div className={styles.row}>
        <Field label="Image name" required>
          <Input
            value={values.imageName}
            onChange={(_, d) => setValues((s) => ({ ...s, imageName: d.value }))}
          />
        </Field>
        <Field label="Image version" required>
          <Input
            value={values.imageVersion}
            onChange={(_, d) => setValues((s) => ({ ...s, imageVersion: d.value }))}
          />
        </Field>
      </div>

      <Field label="Description">
        <Textarea
          rows={3}
          value={values.description}
          onChange={(_, d) => setValues((s) => ({ ...s, description: d.value }))}
        />
      </Field>

      <Field
        label="Required roles"
        hint="App role values (Entra ID app roles, e.g. mcp.engineer) that grant read access. Leave empty to allow any authenticated user."
      >
        <div className={styles.rolesEditor}>
          {values.requiredRoles.length > 0 ? (
            <TagGroup
              aria-label="Required roles"
              onDismiss={(_, data) => removeRole(String(data.value))}
            >
              {values.requiredRoles.map((role) => (
                <Tag
                  key={role}
                  value={role}
                  shape="rounded"
                  appearance="brand"
                  dismissible
                  dismissIcon={{ "aria-label": `Remove ${role}` }}
                  media={<ShieldKeyholeRegular />}
                >
                  {role}
                </Tag>
              ))}
            </TagGroup>
          ) : (
            <Caption1 className={styles.rolesEmpty}>
              No roles required — any authenticated user can read this resource.
            </Caption1>
          )}
          <div className={styles.rolesInputRow}>
            <Input
              value={roleDraft}
              placeholder="e.g. mcp.engineer"
              onChange={(_, d) => setRoleDraft(d.value)}
              onKeyDown={(e) => {
                if (e.key === "Enter" || e.key === ",") {
                  e.preventDefault();
                  addRole(roleDraft);
                } else if (
                  e.key === "Backspace" &&
                  roleDraft === "" &&
                  values.requiredRoles.length > 0
                ) {
                  // Quick-delete the last chip when the input is empty.
                  removeRole(values.requiredRoles[values.requiredRoles.length - 1]);
                }
              }}
            />
            <Button
              type="button"
              appearance="secondary"
              icon={<AddRegular />}
              disabled={!roleDraft.trim()}
              onClick={() => addRole(roleDraft)}
            >
              Add role
            </Button>
          </div>
        </div>
      </Field>

      <Field label="Use workload identity">
        <Switch
          checked={values.useWorkloadIdentity}
          onChange={(_, d) => setValues((s) => ({ ...s, useWorkloadIdentity: d.checked }))}
        />
      </Field>

      <div>
        <Caption1 block>Environment variables</Caption1>
        <div className={styles.envEditor}>
          {envRows.map((row, idx) => (
            <div key={idx} className={styles.envRow}>
              <Input
                placeholder="KEY"
                value={row.key}
                onChange={(_, d) => setEnvRowAt(idx, { key: d.value })}
              />
              <Input
                placeholder="value"
                value={row.value}
                onChange={(_, d) => setEnvRowAt(idx, { value: d.value })}
              />
              <Button
                appearance="subtle"
                type="button"
                onClick={() => setEnvRows((rows) => rows.filter((_, i) => i !== idx))}
              >
                Remove
              </Button>
            </div>
          ))}
          <Button
            appearance="secondary"
            type="button"
            onClick={() => setEnvRows((rows) => [...rows, { key: "", value: "" }])}
          >
            Add variable
          </Button>
        </div>
      </div>

      {children}

      <div className={styles.actions}>
        <Button type="submit" appearance="primary" disabled={submitting}>
          {submitting ? "Saving…" : submitLabel}
        </Button>
        {onCancel && (
          <Button appearance="secondary" onClick={onCancel} disabled={submitting}>
            Cancel
          </Button>
        )}
      </div>
    </form>
  );
}
