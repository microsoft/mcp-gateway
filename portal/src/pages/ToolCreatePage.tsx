// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

import { useNavigate } from "react-router-dom";
import { Caption1, makeStyles, tokens } from "@fluentui/react-components";
import { ToolForm } from "../components/ToolForm";
import { PageHeader } from "../components/PageHeader";
import { useGateway } from "../auth/PortalProvider";

const useStyles = makeStyles({
  card: {
    backgroundColor: tokens.colorNeutralBackground1,
    borderRadius: tokens.borderRadiusLarge,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    boxShadow: tokens.shadow2,
    padding: "24px",
  },
});

export function ToolCreatePage() {
  const styles = useStyles();
  const navigate = useNavigate();
  const { api } = useGateway();

  return (
    <div>
      <PageHeader
        breadcrumbs={[
          { label: "Home", to: "/" },
          { label: "Tools", to: "/tools" },
          { label: "New" },
        ]}
        title="New tool"
        description={
          <Caption1>
            Register a tool with the gateway. The tool definition (name,
            description and input schema) is what MCP clients see when they
            call <code>tools/list</code>.
          </Caption1>
        }
      />
      <div className={styles.card}>
        <ToolForm
          submitLabel="Create tool"
          onSubmit={async (values) => {
            await api.createTool(values);
            navigate(`/tools/${encodeURIComponent(values.name)}`);
          }}
          onCancel={() => navigate("/tools")}
        />
      </div>
    </div>
  );
}
