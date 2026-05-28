// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

import {
  PublicClientApplication,
  type AccountInfo,
  type Configuration,
  type SilentRequest,
  InteractionRequiredAuthError,
} from "@azure/msal-browser";
import type { PortalConfig } from "../api/types";

let instance: PublicClientApplication | undefined;
let initializePromise: Promise<void> | undefined;
let activeAccount: AccountInfo | null = null;

/**
 * Create (or return the cached) MSAL `PublicClientApplication`. Only meaningful
 * when the gateway is running in cloud mode and `config.azureAd` is populated.
 *
 * The function returns `undefined` when the gateway runs in development mode —
 * callers should treat that as "no auth required" and skip token acquisition.
 */
export function getMsalInstance(config: PortalConfig): PublicClientApplication | undefined {
  if (config.isDevelopment || !config.azureAd) return undefined;
  if (instance) return instance;

  const msalConfig: Configuration = {
    auth: {
      clientId: config.azureAd.clientId,
      authority: `https://login.microsoftonline.com/${config.azureAd.tenantId}`,
      // The SPA is mounted under /portal/, so redirect users back there
      // after the interactive sign-in completes.
      redirectUri: window.location.origin + "/portal/",
      postLogoutRedirectUri: window.location.origin + "/portal/",
      navigateToLoginRequestUrl: false,
    },
    cache: {
      cacheLocation: "sessionStorage",
      // Keeps SSO across same-origin tabs without persisting tokens to disk.
      storeAuthStateInCookie: false,
    },
  };

  instance = new PublicClientApplication(msalConfig);
  return instance;
}

/**
 * Lazily initialize the MSAL instance and process any redirect response.
 * Safe to call multiple times — subsequent calls await the original promise.
 */
export async function ensureMsalInitialized(config: PortalConfig): Promise<void> {
  const msal = getMsalInstance(config);
  if (!msal) return;
  if (!initializePromise) {
    initializePromise = (async () => {
      await msal.initialize();
      const response = await msal.handleRedirectPromise();
      if (response?.account) {
        msal.setActiveAccount(response.account);
        activeAccount = response.account;
      } else {
        const accounts = msal.getAllAccounts();
        if (accounts.length > 0) {
          msal.setActiveAccount(accounts[0]);
          activeAccount = accounts[0];
        }
      }
    })();
  }
  return initializePromise;
}

export function getActiveAccount(): AccountInfo | null {
  return activeAccount;
}

/**
 * Acquire an access token for the configured API scope. Falls back to an
 * interactive redirect when no usable token is cached.
 */
export async function acquireToken(config: PortalConfig): Promise<string | undefined> {
  const msal = getMsalInstance(config);
  if (!msal || !config.azureAd) return undefined;
  await ensureMsalInitialized(config);

  const account = msal.getActiveAccount() ?? msal.getAllAccounts()[0];
  if (!account) {
    // Trigger sign-in; the function never returns because the page redirects.
    await msal.loginRedirect({ scopes: config.azureAd.scopes });
    return undefined;
  }

  const request: SilentRequest = {
    account,
    scopes: config.azureAd.scopes,
  };

  try {
    const result = await msal.acquireTokenSilent(request);
    return result.accessToken;
  } catch (err) {
    if (err instanceof InteractionRequiredAuthError) {
      await msal.acquireTokenRedirect(request);
      return undefined;
    }
    throw err;
  }
}

/** Trigger the interactive sign-out flow. */
export async function signOut(config: PortalConfig): Promise<void> {
  const msal = getMsalInstance(config);
  if (!msal) return;
  await msal.logoutRedirect({ postLogoutRedirectUri: window.location.origin + "/portal/" });
}
