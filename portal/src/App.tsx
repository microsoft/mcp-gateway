// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

import { BrowserRouter, Navigate, Route, Routes } from "react-router-dom";
import { Layout } from "./components/Layout";
import { AdaptersPage } from "./pages/AdaptersPage";
import { AdapterCreatePage } from "./pages/AdapterCreatePage";
import { AdapterDetailPage } from "./pages/AdapterDetailPage";
import { ToolsPage } from "./pages/ToolsPage";
import { ToolCreatePage } from "./pages/ToolCreatePage";
import { ToolDetailPage } from "./pages/ToolDetailPage";

export function App() {
  return (
    <BrowserRouter basename="/portal">
      <Routes>
        <Route element={<Layout />}>
          <Route index element={<Navigate to="/adapters" replace />} />
          <Route path="/adapters" element={<AdaptersPage />} />
          <Route path="/adapters/new" element={<AdapterCreatePage />} />
          <Route path="/adapters/:name" element={<AdapterDetailPage />} />
          <Route path="/tools" element={<ToolsPage />} />
          <Route path="/tools/new" element={<ToolCreatePage />} />
          <Route path="/tools/:name" element={<ToolDetailPage />} />
          <Route path="*" element={<Navigate to="/adapters" replace />} />
        </Route>
      </Routes>
    </BrowserRouter>
  );
}
