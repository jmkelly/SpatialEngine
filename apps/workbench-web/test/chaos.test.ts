import { test } from "node:test";
import assert from "node:assert/strict";
import {
  chaosScenarioById,
  chaosScenarios,
  formatExpectation,
  isErrorEnvelope,
  mappingMatches,
  stormScenarioIds,
  summarizeFailure,
} from "../src/chaos.ts";

test("the chaos table covers 400/404/503/499 with one toggle each", () => {
  const scenarios = chaosScenarios();
  const ids = scenarios.map((scenario) => scenario.id);
  assert.deepEqual([...ids].sort(), [...new Set(ids)].sort(), "scenario ids are unique");
  for (const scenario of scenarios) {
    assert.ok(scenario.route.startsWith("/api/"), `${scenario.id} drives a public host route`);
  }
  const byStatus = (status: number) => scenarios.filter((scenario) => scenario.expect.status === status);
  assert.ok(byStatus(400).length >= 3, "the invalid.arguments storm has several 400 toggles");
  assert.ok(byStatus(404).length >= 1, "a not.found toggle exists");
  assert.ok(byStatus(503).length >= 1, "a store.unavailable toggle exists");
  assert.ok(byStatus(499).length >= 1, "a cancellation toggle exists");
});

test("each toggle expects the typed SpatialException code", () => {
  assert.deepEqual(chaosScenarioById("postgis-down")?.expect, { status: 503, code: "store.unavailable" });
  assert.deepEqual(chaosScenarioById("missing-dataset")?.expect, { status: 404, code: "not.found" });
  assert.deepEqual(chaosScenarioById("missing-map")?.expect, { status: 404, code: "not.found" });
  assert.deepEqual(chaosScenarioById("bad-geometry")?.expect, { status: 400, code: "invalid.arguments" });
  assert.deepEqual(chaosScenarioById("bad-crs")?.expect, { status: 400, code: "invalid.arguments" });
  assert.deepEqual(chaosScenarioById("demo-filter")?.expect, { status: 400, code: "invalid.arguments" });
  assert.deepEqual(chaosScenarioById("unknown-store")?.expect, { status: 400, code: "invalid.arguments" });
  assert.deepEqual(chaosScenarioById("cancel-sleep")?.expect, { status: 499, code: null });
  assert.equal(chaosScenarioById("no-such-toggle"), undefined);
});

test("the storm is exactly the 400 toggles", () => {
  const storm = stormScenarioIds();
  assert.ok(storm.length >= 3);
  for (const id of storm) {
    assert.equal(chaosScenarioById(id)?.expect.status, 400);
  }
  assert.deepEqual(
    storm.sort(),
    chaosScenarios().filter((scenario) => scenario.expect.status === 400).map((scenario) => scenario.id).sort(),
  );
});

test("isErrorEnvelope accepts {code, message} and rejects the rest", () => {
  assert.equal(isErrorEnvelope({ code: "not.found", message: "Unknown dataset 'x'." }), true);
  assert.equal(isErrorEnvelope({ code: "not.found", message: "x", traceId: "abc" }), true);
  assert.equal(isErrorEnvelope(null), false);
  assert.equal(isErrorEnvelope("not.found"), false);
  assert.equal(isErrorEnvelope([]), false);
  assert.equal(isErrorEnvelope({}), false);
  assert.equal(isErrorEnvelope({ code: "not.found" }), false);
  assert.equal(isErrorEnvelope({ message: "x" }), false);
  assert.equal(isErrorEnvelope({ code: 404, message: "x" }), false);
  assert.equal(isErrorEnvelope({ code: "not.found", message: null }), false);
});

test("summarizeFailure sorts SpatialApiError-likes, aborts and opaque errors", () => {
  assert.deepEqual(summarizeFailure({ status: 404, code: "not.found", message: "Unknown dataset 'x'." }), {
    kind: "api",
    status: 404,
    code: "not.found",
    message: "Unknown dataset 'x'.",
  });
  assert.deepEqual(summarizeFailure({ status: 499, message: "the host failed with 499" }), {
    kind: "api",
    status: 499,
    code: null,
    message: "the host failed with 499",
  });
  const abort = new DOMException("The operation was aborted.", "AbortError");
  assert.deepEqual(summarizeFailure(abort), { kind: "aborted", status: null, code: null, message: "aborted" });
  assert.deepEqual(summarizeFailure(new Error("boom")), { kind: "other", status: null, code: null, message: "boom" });
  assert.deepEqual(summarizeFailure(null), { kind: "other", status: null, code: null, message: "null" });
});

test("mappingMatches pins status and code, with 499 matching on status alone", () => {
  assert.equal(mappingMatches({ status: 400, code: "invalid.arguments" }, { status: 400, code: "invalid.arguments" }), true);
  assert.equal(mappingMatches({ status: 400, code: "invalid.arguments" }, { status: 400, code: "not.found" }), false);
  assert.equal(mappingMatches({ status: 404, code: "not.found" }, { status: 400, code: "not.found" }), false);
  assert.equal(mappingMatches({ status: 503, code: "store.unavailable" }, { status: null, code: "store.unavailable" }), false);
  assert.equal(mappingMatches({ status: 499, code: null }, { status: 499, code: null }), true);
  assert.equal(mappingMatches({ status: 499, code: null }, { status: 499, code: "http.error" }), true);
  assert.equal(mappingMatches({ status: 499, code: null }, { status: 200, code: null }), false);
});

test("formatExpectation renders the badge string", () => {
  assert.equal(formatExpectation({ status: 503, code: "store.unavailable" }), "503 · store.unavailable");
  assert.equal(formatExpectation({ status: 499, code: null }), "499 · (no envelope)");
});
