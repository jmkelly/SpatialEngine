import { test } from "node:test";
import assert from "node:assert/strict";
import { fieldsFor, operationById, operations } from "../src/forms.ts";
import { attachGeometryBytes, failure, fromBase64, geometryFromBase64, toBase64, withoutOperationResults } from "../src/state-helpers.ts";

/**
 * Operation forms and state helpers (ADR-0033): static per-route field
 * descriptors plus base64 SGEOM plumbing for the map selection.
 */

test("the catalogue lists the nine typed operations", () => {
  assert.deepEqual(
    operations().map((operation) => operation.id),
    ["buffer", "intersection", "validate", "simplify", "transform", "describe", "scan", "query", "sleep"],
  );
});

test("buffer gets a geometry and a numeric distance field", () => {
  const fields = fieldsFor("buffer", { catalogDatasets: [], selectedGeometryLabel: "point-001" });
  const names = fields.map((field) => `${field.name}:${field.kind}`);
  assert.deepEqual(names, ["geometry:geometry", "distance:number", "quadrantSegments:int"]);
});

test("sleep gets an int milliseconds field", () => {
  const fields = fieldsFor("sleep", { catalogDatasets: [] });
  assert.deepEqual(fields.map((field) => field.kind), ["int"]);
});

test("scan gets a dataset select with catalogue options", () => {
  const fields = fieldsFor("scan", { catalogDatasets: ["demo.points", "demo.cities"] });
  assert.equal(fields[0]?.kind, "select");
  assert.deepEqual(fields[0]?.options, ["demo.points", "demo.cities"]);
});

test("query gets a dataset select plus bbox and filter fields", () => {
  const fields = fieldsFor("query", { catalogDatasets: [] });
  assert.deepEqual(
    fields.map((field) => field.name),
    ["dataset", "minx", "miny", "maxx", "maxy", "filter"],
  );
});

test("unknown operations have no fields", () => {
  assert.deepEqual(fieldsFor("mystery", { catalogDatasets: [] }), []);
  assert.equal(operationById("mystery"), undefined);
});

test("geometry bytes attach from the first Geometry attribute and round trip", () => {
  const bytes = new TextEncoder().encode("sgeom-bytes");
  const feature = { attributes: [{ kind: "Geometry", value: bytes }, { kind: "String", value: "x" }] };
  const target: Record<string, string> = {};
  attachGeometryBytes(target, "f1", feature);
  assert.equal(target.f1, "c2dlb20tYnl0ZXM=");
  assert.deepEqual(fromBase64(target.f1!), bytes);
  assert.equal(toBase64(bytes), "c2dlb20tYnl0ZXM=");
});

test("geometry decode of garbage is null", () => {
  assert.equal(geometryFromBase64(null), null);
  assert.equal(geometryFromBase64("!!!"), null);
});

test("failures carry the operation and message", () => {
  const outcome = failure("buffer", "no geometry selected");
  assert.equal(outcome.op, "buffer");
  assert.equal(outcome.ok, false);
  assert.equal(outcome.error, "no geometry selected");
});

function resultFeature(kind: unknown): GeoJSON.Feature {
  return { type: "Feature", properties: kind === undefined ? null : { kind }, geometry: { type: "Point", coordinates: [0, 0] } };
}

test("clearing results drops operation previews but keeps saved results", () => {
  const collection: GeoJSON.FeatureCollection = {
    type: "FeatureCollection",
    features: [resultFeature("operation"), resultFeature("saved"), resultFeature(undefined)],
  };
  const cleared = withoutOperationResults(collection);
  assert.deepEqual(cleared.features.map((feature) => feature.properties), [{ kind: "saved" }, null]);
  // Pure: the input collection is untouched.
  assert.equal(collection.features.length, 3);
});

test("clearing an empty or all-saved result layer is a no-op", () => {
  assert.deepEqual(withoutOperationResults({ type: "FeatureCollection", features: [] }), { type: "FeatureCollection", features: [] });
  const saved: GeoJSON.FeatureCollection = { type: "FeatureCollection", features: [resultFeature("saved")] };
  assert.equal(withoutOperationResults(saved).features.length, 1);
});
