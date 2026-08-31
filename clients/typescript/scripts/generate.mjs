#!/usr/bin/env node
/**
 * The TypeScript SDK generator (plan §12 "generate the TypeScript SDK"):
 * emits `src/generated-types.ts` from the host's OpenAPI description, so the
 * SDK's wire types can never drift from the public HTTP contracts. The
 * source document is the committed snapshot by default (deterministic,
 * CI-safe); point it at a live host's `/openapi/v1.json` to refresh:
 *
 *   node scripts/generate.mjs <openapi.json> <out.ts>
 *   node scripts/generate.mjs scripts/openapi.snapshot.json src/generated-types.ts
 *
 * The emitter is intentionally small: the host API's schemas are flat DTO
 * records (objects, arrays, enums, nullable refs). Anything it cannot model
 * falls back to `unknown` rather than emitting a wrong type.
 */
import { readFileSync, writeFileSync } from "node:fs";

const [source = "scripts/openapi.snapshot.json", output = "src/generated-types.ts"] = process.argv.slice(2);
const document = JSON.parse(readFileSync(source, "utf8"));
const schemas = document.components?.schemas ?? {};

const lines = [
  "// GENERATED FILE — do not edit by hand.",
  "// Regenerate from the host's OpenAPI description: node scripts/generate.mjs <openapi.json> <out.ts>",
  '// The snapshot lives at scripts/openapi.snapshot.json; scripts/check-generated.mjs fails',
  "// when this file has drifted from it.",
  "",
];

for (const [name, schema] of Object.entries(schemas)) {
  lines.push(...emit(name, schema), "");
}

writeFileSync(output, lines.join("\n"));

function emit(name, schema) {
  if (Array.isArray(schema.enum)) {
    const members = schema.enum.map((value) => JSON.stringify(value)).join(" | ");
    return [`export type ${name} = ${members};`];
  }

  if (schema.type === "object" || schema.properties !== undefined) {
    const members = [];
    for (const [property, propSchema] of Object.entries(schema.properties ?? {})) {
      const required = (schema.required ?? []).includes(property);
      const type = emitPropertyType(propSchema);
      members.push(`  ${property}${required ? "" : "?"}: ${type};`);
    }

    const kind = schema.type === "object" && schema.additionalProperties !== undefined ? "Record<string, unknown>" : "";
    return [`export interface ${name} {`, ...members, ...(kind ? [kind] : []), "}"];
  }

  if (schema.type === "string" || schema.type === "number" || schema.type === "boolean" || schema.type === "integer") {
    return [`export type ${name} = ${schema.type === "integer" ? "number" : schema.type};`];
  }

  return [`export type ${name} = unknown;`];
}

function emitPropertyType(propSchema) {
  if (propSchema.$ref) return refName(propSchema.$ref);
  if (Array.isArray(propSchema.type) && propSchema.type.includes("null")) {
    return `${emitPropertyType({ ...propSchema, type: propSchema.type.find((t) => t !== "null") })} | null`;
  }
  if (propSchema.oneOf) {
    const alts = propSchema.oneOf.map((alt) => emitPropertyType(alt));
    const hasNull = alts.includes("null");
    const nonNull = alts.filter((type) => type !== "null");
    if (nonNull.length === 0) return "null";
    return `${nonNull.join(" | ")}${hasNull ? " | null" : ""}`;
  }
  if (propSchema.type === "array") return `${emitPropertyType(propSchema.items ?? {})}[]`;
  if (propSchema.type === "object") return "{ [key: string]: unknown }";
  if (propSchema.type === "integer") return "number";
  if (propSchema.type === "string") return "string";
  if (propSchema.type === "boolean") return "boolean";
  if (propSchema.type === "null") return "null";
  return "unknown";
}

function refName(ref) {
  return ref.split("/").pop().replace(/[^A-Za-z0-9_$]/g, "");
}

console.log(`generated ${output} from ${source} (${Object.keys(schemas).length} schemas)`);