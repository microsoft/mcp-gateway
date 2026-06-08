// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

import React from "react";
import ReactDOM from "react-dom/client";
import { FluentProvider, webLightTheme } from "@fluentui/react-components";
import { App } from "./App";
import { PortalProvider } from "./auth/PortalProvider";
import "./styles.css";

const root = document.getElementById("root");
if (!root) {
  throw new Error("Missing #root element");
}

ReactDOM.createRoot(root).render(
  <React.StrictMode>
    <FluentProvider theme={webLightTheme}>
      <PortalProvider>
        <App />
      </PortalProvider>
    </FluentProvider>
  </React.StrictMode>,
);
