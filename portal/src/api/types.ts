// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// TypeScript projections of the contracts exposed by Microsoft.McpGateway.
// Keep these in sync with the C# DTOs under
// dotnet/Microsoft.McpGateway.Management/src/Contracts/.

export interface AdapterData {
  name: string;
  imageName: string;
  imageVersion: string;
  environmentVariables: Record<string, string>;
  replicaCount: number;
  description: string;
  useWorkloadIdentity: boolean;
  requiredRoles: string[];
}

export interface AdapterResource extends AdapterData {
  id: string;
  createdBy: string;
  createdAt: string;
  lastUpdatedAt: string;
}

export interface AdapterStatus {
  readyReplicas: number | null;
  updatedReplicas: number | null;
  availableReplicas: number | null;
  image: string | null;
  replicaStatus: string | null;
}

export interface PodLogs {
  // The gateway returns the raw log text per pod instance; the precise shape
  // is opaque to the portal so we accept whatever JSON the gateway returns.
  [key: string]: unknown;
}

export interface ToolDefinitionTool {
  name: string;
  description?: string;
  inputSchema?: Record<string, unknown>;
}

export interface ToolDefinition {
  tool: ToolDefinitionTool;
  port: number;
  path: string;
}

export interface ToolData extends AdapterData {
  toolDefinition: ToolDefinition;
}

export interface ToolResource extends ToolData {
  id: string;
  createdBy: string;
  createdAt: string;
  lastUpdatedAt: string;
}

/** Discriminator used by the UI to render the right list/detail view. */
export type ResourceKind = "adapter" | "tool";

/** Runtime configuration returned by GET /portal/config. */
export interface PortalConfig {
  /** True when the gateway is running under the Development auth handler. */
  isDevelopment: boolean;
  /** Public origin of the gateway (used to build MCP endpoint URLs). */
  publicOrigin: string;
  /** Whether the agents/sessions preview endpoints are wired up. */
  agentsEnabled: boolean;
  /** Entra ID configuration, only populated in cloud mode. */
  azureAd?: {
    tenantId: string;
    clientId: string;
    /**
     * Default scope requested by the portal. Defaults to
     * `api://<clientId>/.default` when not provided by the server.
     */
    scopes: string[];
  };
}
