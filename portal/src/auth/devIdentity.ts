// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Synthetic identity used in development mode. The DevelopmentAuthenticationHandler
// on the server side picks up X-Dev-UserId / X-Dev-Name / X-Dev-Roles headers and
// mints a matching ClaimsPrincipal. We persist the choice in localStorage so it
// survives reloads while we exercise role-based behaviour.

const STORAGE_KEY = "mcp-gateway-portal.dev-identity";

export interface DevIdentity {
  userId: string;
  displayName: string;
  /** Comma-separated role list as expected by the dev auth handler. */
  roles: string;
}

export const defaultDevIdentity: DevIdentity = {
  userId: "dev",
  displayName: "Local Developer",
  roles: "mcp.dev",
};

export function loadDevIdentity(): DevIdentity {
  if (typeof window === "undefined") return defaultDevIdentity;
  try {
    const raw = window.localStorage.getItem(STORAGE_KEY);
    if (!raw) return defaultDevIdentity;
    const parsed = JSON.parse(raw) as Partial<DevIdentity>;
    return {
      userId: parsed.userId?.trim() || defaultDevIdentity.userId,
      displayName: parsed.displayName?.trim() || defaultDevIdentity.displayName,
      roles: parsed.roles?.trim() || defaultDevIdentity.roles,
    };
  } catch {
    return defaultDevIdentity;
  }
}

export function saveDevIdentity(identity: DevIdentity): void {
  if (typeof window === "undefined") return;
  window.localStorage.setItem(STORAGE_KEY, JSON.stringify(identity));
}

export function devIdentityHeaders(identity: DevIdentity): HeadersInit {
  // Only forward headers that are actually populated to avoid clobbering the
  // gateway's own defaults with empty strings.
  const headers: Record<string, string> = {};
  if (identity.userId.trim()) headers["X-Dev-UserId"] = identity.userId.trim();
  if (identity.displayName.trim()) headers["X-Dev-Name"] = identity.displayName.trim();
  if (identity.roles.trim()) headers["X-Dev-Roles"] = identity.roles.trim();
  return headers;
}
