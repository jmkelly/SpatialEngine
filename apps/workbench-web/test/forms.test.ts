import { test } from "node:test";
import assert from "node:assert/strict";
import { fieldsFor } from "../src/forms.ts";
import { buildWireArguments } from "../src/wire-args.ts";
import { attachGeometryWire, outcomeFromResponse, resourceTokenFrom } from "../src/state-helpers.ts";

/**
 * Form generation and wire argument building (Phase 10): known capability
 * shapes get declarative fields, and form values become the wire-encoded
 * invocation arguments ($geometry tags, $i64 int64s) the host codec reads.
 */

const capability = (id: string, inputSchema = "scalar") => ({
  id,
  purpose: "test",
  inputSchema,
  outputSchema: "scalar",
  errors: [],
  requiredPermissions: [],
  traits: ["Cancellable"],
  providers: [{ id: "demo@1", health: "healthy" }],
});

test("buffer gets a geometry and a numeric distance field", () => {
  const fields = fieldsFor(capability("spatial.geometry.buffer@1"), { catalogDatasets: [], selectedGeometryLabel: "point-001" });
  const names = fields.map((field) => `${field.name}:${field.kind}`);
  assert.deepEqual(names, ["geometry:geometry", "distance:number", "quadrantSegments:int"]);
});

test("the sleep gets an int64 milliseconds field", () => {
  const fields = fieldsFor(capability("spatial.demo.sleep@1"), { catalogDatasets: [] });
  assert.deepEqual(fields.map((field) => field.kind), ["int"]);
});

test("scan gets a dataset select plus an all-or-none bbox", () => {
  const fields = fieldsFor(capability("spatial.feature.scan@1", "dataset.identity"), { catalogDatasets: ["demo.points", "demo.cities"] });
  assert.equal(fields[0]!.kind, "select");
  assert.deepEqual(fields[0]!.options, ["demo.points", "demo.cities"]);
});

test("unknown shapes fall back to a JSON editor", () => {
  const fields = fieldsFor(capability("spatial.mystery@1", "widget"), { catalogDatasets: [] });
  assert.deepEqual(fields.map((field) => field.kind), ["json"]);
});

test("wire arguments encode geometry tags, int64s and numbers", () => {
  const args = buildWireArguments(
    [
      { name: "geometry", label: "Geometry", kind: "geometry", required: true },
      { name: "distance", label: "Distance", kind: "number", required: true },
      { name: "quadrantSegments", label: "Segments", kind: "int", required: false },
    ],
    { geometry: undefined, distance: "1.5", quadrantSegments: "8" },
    { $geometry: "QUJD" },
  );
  assert.deepEqual(args, { geometry: { $geometry: "QUJD" }, distance: 1.5, quadrantSegments: { $i64: "8" } });
});

test("wire arguments skip empty optional values", () => {
  const args = buildWireArguments(
    [{ name: "pattern", label: "Pattern", kind: "text", required: false }],
    { pattern: "" },
    null,
  );
  assert.deepEqual(args, {});
});

test("a completed response becomes an outcome with provenance", () => {
  const response = {
    kind: "completed",
    capability: "spatial.geometry.buffer@1",
    ok: true,
    result: { $geometry: "QUJD" },
    error: null,
    provenance: { capability: "spatial.geometry.buffer@1", provider: "nts@1", step: "FirstHealthy", startedAt: "2026-09-01T00:00:00Z", durationMs: 2, deadline: null, jobId: null },
    job: null,
  };
  const outcome = outcomeFromResponse(response as never);
  assert.equal(outcome.provider, "nts@1");
  assert.equal(outcome.ok, true);
  assert.deepEqual(outcome.result, { $geometry: "QUJD" });
});

test("resource tokens come from tagged results", () => {
  const response = {
    result: { $resource: { token: "abc-123", kind: "stream", owner: "demo@1", createdAt: "2026-09-01T00:00:00Z" } },
  };
  assert.equal(resourceTokenFrom(response as never), "abc-123");
  assert.equal(resourceTokenFrom({ result: null } as never), null);
  assert.equal(resourceTokenFrom({ result: 42 } as never), null);
});

test("geometry wires attach from the first Geometry attribute", () => {
  const bytes = new TextEncoder().encode("sgeom-bytes");
  const feature = { attributes: [{ kind: "Geometry", value: bytes }, { kind: "String", value: "x" }] } as never;
  const target: Record<string, unknown> = {};
  attachGeometryWire(target, "f1", feature);
  assert.deepEqual(target.f1, { $geometry: "c2dlb20tYnl0ZXM=" });
});