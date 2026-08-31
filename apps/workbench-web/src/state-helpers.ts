import { fromBase64, type InvocationResponse } from "@spatial/client";
import { decodeSgeom, type DecodedGeometry } from "./sgeom.ts";
import type { RunOutcome } from "./run-outcome.ts";

/**
 * Pure helpers shared by the workbench state and its unit tests (kept free of
 * JSX so Node can type-strip them directly): response-to-outcome mapping,
 * resource token extraction, wire geometry attachment and wire decode.
 */

/** Records the first geometry attribute of a batch feature as its wire <c>{$geometry}</c> tag. */
export function attachGeometryWire(
  target: Record<string, unknown>,
  featureId: string,
  feature: { attributes: { kind: string; value?: unknown }[] },
): void {
  for (const attribute of feature.attributes) {
    if (attribute?.kind === "Geometry" && attribute.value instanceof Uint8Array) {
      target[featureId] = { $geometry: toBase64(attribute.value) };
      return;
    }
  }
}

/** Maps a completed invocation response to a run outcome. */
export function outcomeFromResponse(response: InvocationResponse): RunOutcome {
  return {
    capability: response.capability,
    provider: response.provenance?.provider ?? null,
    ok: response.ok,
    error: response.error?.message ?? (response.ok ? null : "invocation failed"),
    result: response.result,
    jobId: null,
    step: response.provenance?.step ?? null,
    finishedAt: new Date().toISOString(),
  };
}

/** Reads the resource token from a <c>{$resource}</c>-tagged result, or null. */
export function resourceTokenFrom(response: { result: unknown }): string | null {
  const result = response.result;
  if (typeof result !== "object" || result === null) return null;
  const tagged = result as Record<string, unknown>;
  if (typeof tagged.$resource !== "object" || tagged.$resource === null) return null;
  const resource = tagged.$resource as Record<string, unknown>;
  return typeof resource.token === "string" ? resource.token : null;
}

/** Decodes a wire geometry result (<c>{$geometry}</c> tag) to GeoJSON, or null. */
export function geometryFromWire(result: unknown): DecodedGeometry | null {
  if (typeof result !== "object" || result === null) return null;
  const tagged = result as Record<string, unknown>;
  if (typeof tagged.$geometry !== "string") return null;
  try {
    return decodeSgeom(fromBase64(tagged.$geometry));
  } catch {
    return null;
  }
}

export function describe(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

export function failure(message: string, jobId: string | null = null): RunOutcome {
  return { capability: "", provider: null, ok: false, error: message, result: null, jobId, step: null, finishedAt: new Date().toISOString() };
}

function toBase64(bytes: Uint8Array): string {
  let binary = "";
  const chunk = 0x8000;
  for (let i = 0; i < bytes.length; i += chunk) {
    binary += String.fromCharCode(...bytes.subarray(i, Math.min(i + chunk, bytes.length)));
  }
  return btoa(binary);
}