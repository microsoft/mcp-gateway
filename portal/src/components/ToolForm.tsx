// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

import { useState } from "react";
import {
  Field,
  Input,
  Textarea,
  Caption1,
  Subtitle2,
  makeStyles,
  tokens,
  SpinButton,
} from "@fluentui/react-components";
import type { ToolData, ToolDefinition } from "../api/types";
import { AdapterFormFields } from "./AdapterFormFields";

const useStyles = makeStyles({
  section: {
    padding: "16px",
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    display: "grid",
    gap: "12px",
  },
  row: {
    display: "grid",
    gap: "12px",
    gridTemplateColumns: "1fr 1fr",
  },
});

interface Props {
  initial?: Partial<ToolData>;
  disableName?: boolean;
  submitLabel: string;
  onSubmit: (values: ToolData) => Promise<void>;
  onCancel?: () => void;
}

const blankDefinition: ToolDefinition = {
  tool: { name: "", description: "", inputSchema: { type: "object", properties: {} } },
  port: 443,
  path: "/score",
};

export function ToolForm({ initial, disableName, submitLabel, onSubmit, onCancel }: Props) {
  const styles = useStyles();
  const [definition, setDefinition] = useState<ToolDefinition>(() => ({
    ...blankDefinition,
    ...(initial?.toolDefinition ?? {}),
    tool: {
      ...blankDefinition.tool,
      ...(initial?.toolDefinition?.tool ?? {}),
    },
  }));
  const [schemaText, setSchemaText] = useState<string>(() =>
    JSON.stringify(initial?.toolDefinition?.tool?.inputSchema ?? blankDefinition.tool.inputSchema, null, 2),
  );
  const [schemaError, setSchemaError] = useState<string | undefined>();

  return (
    <AdapterFormFields
      initial={initial}
      disableName={disableName}
      submitLabel={submitLabel}
      onSubmit={async (base) => {
        // Validate the schema JSON once at submit time so the user gets
        // immediate feedback inline instead of failing server-side.
        let schema: Record<string, unknown> | undefined;
        try {
          schema = schemaText.trim() ? JSON.parse(schemaText) : undefined;
          setSchemaError(undefined);
        } catch (err) {
          setSchemaError((err as Error).message);
          throw err;
        }

        // The server requires Tool.Name === ToolData.Name to enforce the
        // 1:1 mapping between the deployment and the MCP tool it exposes.
        const payload: ToolData = {
          ...base,
          toolDefinition: {
            ...definition,
            tool: {
              ...definition.tool,
              name: base.name,
              inputSchema: schema,
            },
          },
        };
        await onSubmit(payload);
      }}
      onCancel={onCancel}
    >
      <div className={styles.section}>
        <Subtitle2>Tool definition</Subtitle2>
        <Caption1>
          This is what the gateway advertises to MCP clients when they call
          {" "}<code>tools/list</code>. The tool name is locked to the deployment name.
        </Caption1>
        <Field label="Description">
          <Textarea
            rows={2}
            value={definition.tool.description ?? ""}
            onChange={(_, d) =>
              setDefinition((s) => ({ ...s, tool: { ...s.tool, description: d.value } }))
            }
          />
        </Field>
        <div className={styles.row}>
          <Field label="Execution port" required>
            <SpinButton
              min={1}
              max={65535}
              value={definition.port}
              onChange={(_, d) =>
                setDefinition((s) => ({ ...s, port: d.value ?? s.port }))
              }
            />
          </Field>
          <Field label="Execution path" required>
            <Input
              value={definition.path}
              onChange={(_, d) => setDefinition((s) => ({ ...s, path: d.value }))}
            />
          </Field>
        </div>
        <Field
          label="Input schema (JSON)"
          hint="JSON Schema describing the tool's arguments. Leave empty for no arguments."
          validationMessage={schemaError}
          validationState={schemaError ? "error" : "none"}
        >
          <Textarea
            rows={10}
            textarea={{ style: { fontFamily: tokens.fontFamilyMonospace, fontSize: 13 } }}
            value={schemaText}
            onChange={(_, d) => setSchemaText(d.value)}
          />
        </Field>
      </div>
    </AdapterFormFields>
  );
}
