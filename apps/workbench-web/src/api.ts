import { SpatialClient } from "@spatial/client";

/**
 * The workbench's only channel to the host (frontend-boundary.md): the
 * generated TypeScript SDK over the public host API. The host URL is the
 * same origin by default (the host serves the built workbench, ADR-0031);
 * VITE_SPATIAL_HOST_URL overrides it for development against a remote host.
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
  return new SpatialClient(hostBaseUrl());
}