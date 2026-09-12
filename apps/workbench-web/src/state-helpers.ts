import { decodeSgeom, type DecodedGeometry } from "./sgeom.ts";
import type { RunOutcome } from "./run-outcome.ts";

/**
 * Pure helpers shared by the workbench state and its unit tests (kept free of
 * JSX so Node can type-strip them directly): base64 bytes handling and
 * SGEOM-to-GeoJSON decode.
 */

/** Records a feature's geometry bytes (base64 SGEOM) for operation arguments. */
export function attachGeometryBytes(
  target: Record<string, string>,
  featureId: string,
  feature: { attributes: { kind: string; value?: unknown }[] },
): void {
  for (const attribute of feature.attributes) {
    if (attribute?.kind === "Geometry" && attribute.value instanceof Uint8Array) {
      target[featureId] = toBase64(attribute.value);
      return;
    }
  }
}

/** Decodes base64 SGEOM bytes to GeoJSON, or null. */
export function geometryFromBase64(base64: string | null): DecodedGeometry | null {
  if (base64 === null) return null;
  try {
    return decodeSgeom(fromBase64(base64));
  } catch {
    return null;
  }
}

export function describe(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

/**
 * Drops transient operation results from the map's result layer while keeping
 * persisted saved results. Operation previews are tagged `kind: "operation"`
 * by `applyResultToMap`; saved results are tagged `kind: "saved"`. Features
 * without a kind are kept (the safe default). Pure so node can test it.
 */
export function withoutOperationResults(collection: GeoJSON.FeatureCollection): GeoJSON.FeatureCollection {
  return {
    type: "FeatureCollection",
    features: collection.features.filter((feature) => feature.properties?.kind !== "operation"),
  };
}

export function failure(op: string, message: string): RunOutcome {
  return { op, ok: false, error: message, geometryBase64: null, summary: null, finishedAt: new Date().toISOString() };
}

export function fromBase64(base64: string): Uint8Array {
  const binary = atob(base64);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
  return bytes;
}

export function toBase64(bytes: Uint8Array): string {
  let binary = "";
  const chunk = 0x8000;
  for (let i = 0; i < bytes.length; i += chunk) {
    binary += String.fromCharCode(...bytes.subarray(i, Math.min(i + chunk, bytes.length)));
  }
  return btoa(binary);
}
