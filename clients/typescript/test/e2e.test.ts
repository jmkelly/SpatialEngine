import { test } from "node:test";
import assert from "node:assert/strict";
import { SpatialClient } from "../src/client.ts";
import { SpatialApiError } from "../src/errors.ts";
import type { InvocationRequest } from "../src/generated-types.ts";
import { encode, encodeGeometry } from "../src/wire.ts";

/**
 * The live-host E2E: drives the real independently executable Spatial.Host
 * with the real NetTopologySuite worker package — the automated browser-
 * style client the phase requires (plan §16 Phase 9 "confirm the host runs
 * independently through browser and automated clients"). Skipped unless
 * SPATIAL_HOST_URL points at a running host with plugins loaded (the
 * eng/e2e-web.sh script sets it up).
 */
const HOST = process.env.SPATIAL_HOST_URL;
const ENABLED = HOST !== undefined && HOST.length > 0;

test("the live host serves capabilities, a buffer invocation and structured errors", { skip: !ENABLED ? "set SPATIAL_HOST_URL to a running host with the nts@1 package" : false }, async () => {
  const client = new SpatialClient(HOST!);

  const plugins = await client.getPlugins();
  assert.ok(plugins.some((plugin) => plugin.id === "nts@1"), "the nts@1 worker package is loaded");

  const capabilities = await client.getCapabilities();
  assert.ok(capabilities.some((capability) => capability.id === "spatial.geometry.buffer@1"));

  // Buffering a point through the isolated worker: the geometry is a real
  // canonical SGEOM point (FORMAT: "SGEOM" + version + layout Xy + type Point
  // + no CRS + present flag + two zero doubles) — the codec the NTS worker
  // decodes with.
  const invocation: InvocationRequest = {
    capability: "spatial.geometry.buffer@1",
    arguments: { geometry: encodeGeometry(pointAtOrigin()), distance: 1.5 },
  };
  const response = await client.invoke(invocation);
  assert.equal(response.kind, "completed", JSON.stringify(response.error));
  assert.equal(response.ok, true, response.error?.message ?? "invocation failed");
  assert.equal(response.provenance?.provider, "nts@1");
  const result = response.result;
  assert.ok(typeof result === "object" && result !== null && "$geometry" in result, "the buffered result is a geometry");

  // An unknown capability is a completed error, not an HTTP failure.
  const missing = await client.invoke({ capability: "spatial.missing@9" });
  assert.equal(missing.ok, false);
  assert.equal(missing.error?.code, "capability.not.found");

  // A missing resource is a 404 through the SDK.
  await assert.rejects(() => client.getResource("00000000000000000000000000000000"), SpatialApiError);
});

/**
 * The canonical SGEOM encoding of an empty-array-free XY point at the origin:
 * magic + version + layout(0=Xy) + type(1=Point) + crs(0=none) + present(1)
 * + x(0.0) + y(0.0). Built in TS so the E2E needs no .NET geometry helper;
 * the .NET-produced reference vector in feature-batch.test.ts pins the codec
 * alignment.
 */
function pointAtOrigin(): Uint8Array {
  const bytes = new Uint8Array(26);
  bytes.set(new TextEncoder().encode("SGEOM"), 0);
  bytes[5] = 1; // format version
  bytes[6] = 0; // CoordinateLayout.Xy
  bytes[7] = 1; // GeometryType.Point
  bytes[8] = 0; // no CRS
  bytes[9] = 1; // coordinate present
  return bytes;
}