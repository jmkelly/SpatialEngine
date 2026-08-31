/**
 * The inline value wire codec (ADR-0020/0025/0030) mirrored from the SDK's
 * .NET ValueCodec: the one JSON encoding the worker protocol and the HTTP
 * host API share. Scalars cross as JSON; 64-bit integers as {$i64}; binary
 * as {$bytes}; canonical geometry and resource handles keep their tags.
 * Anything else (arrays, plain objects, feature batches) is rejected — those
 * cross as bounded streams, never inline.
 */
export type WireI64 = { $i64: string };
export type WireBytes = { $bytes: string };
export type WireGeometry = { $geometry: string };
export type WireResource = {
  $resource: {
    token: string;
    kind: string;
    owner: string;
    createdAt: string;
  };
};
export type WireCrs = { $crs: Record<string, unknown> };
export type WireValue = null | boolean | number | string | WireI64 | WireBytes | WireGeometry | WireResource | WireCrs;

/**
 * A geometry as canonical SGEOM bytes, expressed explicitly so the encoder
 * emits the <c>$geometry</c> tag (a plain Uint8Array encodes as <c>$bytes</c>)
 * — the canonical binary interchange of ADR-0020.
 */
export class GeometryValue {
  readonly bytes: Uint8Array;

  constructor(bytes: Uint8Array) {
    this.bytes = bytes;
  }
}

/** Encodes canonical SGEOM bytes as a <c>$geometry</c> wire value. */
export function encodeGeometry(bytes: Uint8Array): WireGeometry {
  return { $geometry: toBase64(bytes) };
}

/** Decodes a <c>$geometry</c> wire value back to its canonical SGEOM bytes. */
export function decodeGeometry(wire: WireGeometry): Uint8Array {
  return fromBase64(wire.$geometry);
}

/** Encodes one value to its wire form. Throws for values the codec does not carry. */
export function encode(value: unknown): WireValue | null {
  if (value === null || value === undefined) return null;
  switch (typeof value) {
    case "boolean":
      return value;
    case "number":
      if (!Number.isFinite(value)) {
        throw new Error(`cannot encode ${value}: the inline codec does not carry non-finite numbers`);
      }
      return value;
    case "string":
      return value;
    case "bigint":
      return { $i64: value.toString() };
  }
  if (value instanceof Uint8Array) return { $bytes: toBase64(value) };
  if (value instanceof GeometryValue) return { $geometry: toBase64(value.bytes) };
  if (isTagged(value, "$i64")) return value as WireI64;
  if (isTagged(value, "$bytes")) return value as WireBytes;
  if (isTagged(value, "$geometry")) return value as WireGeometry;
  if (isTagged(value, "$resource")) return value as WireResource;
  if (isTagged(value, "$crs")) return value as WireCrs;
  throw new Error(
    `cannot encode ${describe(value)}: the inline codec carries scalars, \$i64, \$bytes, \$geometry, \$crs and \$resource only; ` +
      "feature batches and other spatial values cross as streams",
  );
}

/**
 * Decodes one wire node back to a value: JSON scalars pass through, {$i64}
 * becomes a bigint, {$bytes} a Uint8Array, and tagged geometry/resource/crs
 * values are returned as their tagged objects (Phase 10 adapters decode
 * geometry bytes).
 */
export function decode(node: unknown): unknown {
  if (node === null || typeof node !== "object" || Array.isArray(node)) return node;
  const tagged = node as Record<string, unknown>;
  if ("$i64" in tagged) return BigInt(tagged.$i64 as string);
  if ("$bytes" in tagged) return fromBase64(tagged.$bytes as string);
  if ("$geometry" in tagged) return tagged as WireGeometry;
  if ("$resource" in tagged) return tagged as WireResource;
  if ("$crs" in tagged) return tagged as WireCrs;
  return node;
}

function isTagged(value: unknown, tag: string): boolean {
  return typeof value === "object" && value !== null && tag in (value as Record<string, unknown>);
}

function describe(value: unknown): string {
  if (value === null) return "null";
  if (Array.isArray(value)) return "an array";
  if (value instanceof Uint8Array) return "a Uint8Array";
  const type = typeof value;
  return type === "object" ? `an object (${(value as { constructor?: { name?: string } }).constructor?.name ?? "?"})` : `a ${type}`;
}

/** Bytes -> base64, chunked so large payloads do not overflow the call stack. */
export function toBase64(bytes: Uint8Array): string {
  let binary = "";
  const chunk = 0x8000;
  for (let i = 0; i < bytes.length; i += chunk) {
    binary += String.fromCharCode(...bytes.subarray(i, Math.min(i + chunk, bytes.length)));
  }
  return btoa(binary);
}

/** base64 -> bytes. */
export function fromBase64(text: string): Uint8Array {
  const binary = atob(text);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
  return bytes;
}