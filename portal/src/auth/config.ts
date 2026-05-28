// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

import type { PortalConfig } from "../api/types";

const CONFIG_ENDPOINT = "/portal/config";

let cached: Promise<PortalConfig> | undefined;

/**
 * Load the gateway's runtime configuration. The portal can't know up front
 * whether the gateway is in dev or cloud mode, so the result determines
 * whether MSAL is initialized and how API calls authenticate.
 *
 * The promise is cached so the config is only fetched once per page load.
 */
export function loadPortalConfig(): Promise<PortalConfig> {
  if (!cached) {
    cached = fetch(CONFIG_ENDPOINT, { credentials: "same-origin" })
      .then(async (res) => {
        if (!res.ok) {
          throw new Error(await formatHttpError(res));
        }
        const raw = (await res.json()) as PortalConfig;
        return normalize(raw);
      })
      .catch((err) => {
        // Don't poison the cache — let the caller retry next time.
        cached = undefined;
        throw err;
      });
  }
  return cached;
}

async function formatHttpError(res: Response): Promise<string> {
  const head = `Failed to load portal config (${res.status} ${res.statusText}).`;
  // ASP.NET Core ProblemDetails responses include {title, detail, ...}.
  // Surface them when present so the operator sees the real cause instead
  // of a bare HTTP status. Body might also be plain text or empty.
  let body = "";
  try {
    body = await res.text();
  } catch {
    return head;
  }
  if (!body) return head;
  try {
    const parsed = JSON.parse(body) as { title?: string; detail?: string };
    const fragments = [parsed.title, parsed.detail].filter(Boolean);
    if (fragments.length > 0) {
      return `${head} ${fragments.join(": ")}`;
    }
  } catch {
    // not JSON — fall through and append the raw body
  }
  const trimmed = body.length > 300 ? `${body.slice(0, 300)}…` : body;
  return `${head} ${trimmed}`;
}

function normalize(raw: PortalConfig): PortalConfig {
  const azureAd = raw.azureAd;
  if (azureAd && (!azureAd.scopes || azureAd.scopes.length === 0)) {
    return {
      ...raw,
      azureAd: {
        ...azureAd,
        scopes: [`api://${azureAd.clientId}/.default`],
      },
    };
  }
  return raw;
}
