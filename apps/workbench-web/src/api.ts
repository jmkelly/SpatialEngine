import { SpatialClient } from "@spatial/client";

/**
 * The workbench's only channel to the host (frontend-boundary.md): the
 * generated TypeScript SDK over the public host API. The host URL is the
 * same origin by default (the host serves the built workbench, ADR-0031);
 * VITE_SPATIAL_HOST_URL overrides it for development against a remote host.
 */
export function createClient(): SpatialClient {
  const baseUrl = (import.meta.env.VITE_SPATIAL_HOST_URL as string | undefined) ?? "";
  return new SpatialClient(baseUrl);
}