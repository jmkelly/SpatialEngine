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

test("the live host renders a styled raster and advertises its capabilities", { skip: !ENABLED ? "set SPATIAL_HOST_URL to a running host" : false }, async () => {
  const client = new SpatialClient(HOST!);

  const capabilities = await client.renderCapabilities();
  assert.ok(capabilities.formats.includes("png"), "png is advertised");
  assert.ok(Number(capabilities.maxPixels) > 0);

  const image = await client.render({
    viewport: { minX: -10, minY: 35, maxX: 30, maxY: 60, width: 400, height: 250, crs: "EPSG:4326" },
    style: {
      version: 8,
      layers: [
        { id: "bg", type: "background", paint: { "background-color": "#101820" } },
        { id: "cities", type: "circle", "source-layer": "demo.cities", paint: { "circle-color": "#ffd166", "circle-radius": 6 } },
      ],
    },
    layers: [{ dataset: "demo.cities", store: "demo" }],
    format: "png",
  });

  assert.equal(image.mediaType, "image/png");
  assert.equal(image.format, "png");
  assert.equal(image.width, 400);
  assert.equal(image.height, 250);
  assert.equal(image.bytes[0], 0x89, "a PNG signature");
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
