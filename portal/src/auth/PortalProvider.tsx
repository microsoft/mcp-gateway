// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

import {
  createContext,
  useContext,
  useEffect,
  useMemo,
  useState,
  type ReactNode,
} from "react";
import { Spinner, MessageBar, MessageBarBody, MessageBarTitle } from "@fluentui/react-components";
import type { PortalConfig } from "../api/types";
import { GatewayApi } from "../api/client";
import { loadPortalConfig } from "./config";
import { ensureMsalInitialized } from "./msal";

interface PortalContextValue {
  config: PortalConfig;
  api: GatewayApi;
}

const PortalContext = createContext<PortalContextValue | undefined>(undefined);

/**
 * Loads the runtime config (and, in cloud mode, initializes MSAL) before
 * rendering its children. Until those promises settle we render a spinner so
 * components downstream can safely assume `useGateway()` returns a real value.
 */
export function PortalProvider({ children }: { children: ReactNode }) {
  const [config, setConfig] = useState<PortalConfig | null>(null);
  const [error, setError] = useState<Error | null>(null);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const loaded = await loadPortalConfig();
        // Cloud mode: complete the MSAL redirect handshake before we let
        // pages start firing authenticated requests.
        await ensureMsalInitialized(loaded);
        if (!cancelled) setConfig(loaded);
      } catch (err) {
        if (!cancelled) setError(err instanceof Error ? err : new Error(String(err)));
      }
    })();
    return () => {
      cancelled = true;
    };
  }, []);

  const value = useMemo<PortalContextValue | null>(
    () => (config ? { config, api: new GatewayApi(config) } : null),
    [config],
  );

  if (error) {
    return (
      <div style={{ padding: 24 }}>
        <MessageBar intent="error">
          <MessageBarBody>
            <MessageBarTitle>Unable to start the portal</MessageBarTitle>
            {error.message}
          </MessageBarBody>
        </MessageBar>
      </div>
    );
  }

  if (!value) {
    return (
      <div style={{ display: "flex", alignItems: "center", justifyContent: "center", height: "100vh" }}>
        <Spinner label="Loading MCP Gateway portal…" />
      </div>
    );
  }

  return <PortalContext.Provider value={value}>{children}</PortalContext.Provider>;
}

export function useGateway(): PortalContextValue {
  const ctx = useContext(PortalContext);
  if (!ctx) {
    throw new Error("useGateway must be used inside <PortalProvider>.");
  }
  return ctx;
}
