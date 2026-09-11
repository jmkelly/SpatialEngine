import { test } from "node:test";
import assert from "node:assert/strict";
import { SpatialClient } from "../src/client.ts";
import { SpatialApiError } from "../src/errors.ts";

/**
 * The live-host E2E: drives the real independently executable Spatial.Host
 * with its in-process services. Skipped unless SPATIAL_HOST_URL points at a
 * running host (the eng/e2e-web.sh script sets it up).
 */
const HOST = process.env.SPATIAL_HOST_URL;
const ENABLED = HOST !== undefined && HOST.length > 0;

test("the live host serves buffer, catalogue, scan and structured errors", { skip: !ENABLED ? "set SPATIAL_HOST_URL to a running host" : false }, async () => {
  const client = new SpatialClient(HOST!);

  // Buffering a point through the in-process operations: the geometry is a
  // real canonical SGEOM point (magic + version + layout Xy + type Point +
  // no CRS + present flag + two zero doubles).
  const buffered = await client.buffer(pointAtOrigin(), 1.5);
  assert.ok(buffered.length > pointAtOrigin().length, "buffering a point grows the payload");

  // An invalid argument is a structured 400, not a transport failure.
  await assert.rejects(() => client.buffer(pointAtOrigin(), 1.5, 0), (error: unknown) => {
    assert.ok(error instanceof SpatialApiError);
    assert.equal((error as SpatialApiError).code, "invalid.arguments");
    return true;
  });

  const catalogue = await client.catalogue();
  assert.ok(catalogue.datasets.some((dataset) => dataset.id === "demo.points"), "the demo catalogue is served");

  const batches = await client.scan("demo.points");
  const count = batches.reduce((sum, batch) => sum + batch.features.length, 0);
  assert.equal(count, 110);

  const description = await client.describeCrs("EPSG:4326");
  assert.equal(description.code, "4326");
});

/**
 * The canonical SGEOM encoding of an XY point at the origin: magic +
 * version + layout(0=Xy) + type(1=Point) + crs(0=none) + present(1) + x(0.0)
 * + y(0.0).
 */
function pointAtOrigin(): Uint8Array {
  const bytes = new Uint8Array(26);
  bytes.set(new TextEncoder().encode("SGEOM"), 0);
  bytes[5] = 1;
  bytes[6] = 0;
  bytes[7] = 1;
  bytes[8] = 0;
  bytes[9] = 1;
  return bytes;
}
