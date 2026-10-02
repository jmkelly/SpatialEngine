import { SpatialClient } from "@spatial/client";

/**
 * The workbench's only channel to the host (frontend-boundary.md): the
 * generated TypeScript SDK over the public host API. The host URL is the
 * same origin by default (the host serves the built workbench, ADR-0031);
 * VITE_SPATIAL_HOST_URL overrides it for development against a remote host.
 * In local `vite dev` leave it unset: the dev server proxies /api, /arcgis,
 * /health and /openapi to VITE_DEV_HOST (default http://127.0.0.1:5199), so
 * the same-origin base still reaches the host port.
 *
 * `hostBaseUrl` exposes the same base for the composer's copyable endpoint
 * URLs, so they point at the host the SDK actually talks to.
 */
export function hostBaseUrl(): string {
  const configured = (import.meta.env.VITE_SPATIAL_HOST_URL as string | undefined) ?? "";
  if (configured !== "") return configured.replace(/\/+$/, "");
  return typeof window === "undefined" ? "" : window.location.origin;
}

export function createClient(): SpatialClient {
  const client = new SpatialClient(hostBaseUrl());
  // The token cache is opt-in: only a login that explicitly asks to remember
  // writes here. Keeping the default memory-only makes accidental persistence
  // less likely while allowing a browser session to resume after navigation.
  if (typeof window !== "undefined") {
    const token = window.localStorage.getItem("spatial.auth.token");
    if (token) client.setToken(token);
  }
  return client;
}
