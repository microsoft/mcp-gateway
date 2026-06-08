// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

import { useNavigate } from "react-router-dom";
import { makeStyles, tokens } from "@fluentui/react-components";
import { AdapterFormFields } from "../components/AdapterFormFields";
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

export function AdapterCreatePage() {
  const styles = useStyles();
  const navigate = useNavigate();
  const { api } = useGateway();

  return (
    <div>
      <PageHeader
        breadcrumbs={[
          { label: "Home", to: "/" },
          { label: "MCP Servers", to: "/adapters" },
          { label: "New" },
        ]}
        title="New MCP server"
        description="Deploy a new MCP server (adapter) to the gateway. The image must already be available to the gateway's container registry."
      />
      <div className={styles.card}>
        <AdapterFormFields
          submitLabel="Create adapter"
          onSubmit={async (values) => {
            await api.createAdapter(values);
            navigate(`/adapters/${encodeURIComponent(values.name)}`);
          }}
          onCancel={() => navigate("/adapters")}
        />
      </div>
    </div>
  );
}
