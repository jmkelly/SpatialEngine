import type { FormField } from "./forms.ts";

/**
 * Turns capability form values into wire-encoded invocation arguments (Phase
 * 10): the inline value codec of ADR-0030 carries scalars, {$i64} int64s and
 * {$geometry} tags — geometry fields inject the selected feature's wire tag,
 * int fields become $i64, numbers stay numbers, and JSON fields are parsed.
 */
export function buildWireArguments(
  fields: FormField[],
  values: Record<string, unknown>,
  selectedGeometryWire: unknown,
): Record<string, unknown> {
  const request: Record<string, unknown> = {};
  for (const field of fields) {
    const value = values[field.name];
    if (field.kind === "geometry") {
      if (selectedGeometryWire !== null && selectedGeometryWire !== undefined) request[field.name] = selectedGeometryWire;
      continue;
    }
    if (value === undefined || value === null || (typeof value === "string" && value === "")) continue;
    if (field.kind === "int") {
      const numeric = Number(value);
      if (Number.isFinite(numeric)) request[field.name] = { $i64: String(Math.trunc(numeric)) };
      continue;
    }
    if (field.kind === "json") {
      if (typeof value === "string") {
        const trimmed = value.trim();
        if (trimmed !== "") request[field.name] = JSON.parse(trimmed);
      }
      continue;
    }
    request[field.name] = field.kind === "number" ? Number(value) : value;
  }
  return request;
}