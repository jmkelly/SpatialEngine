import { test } from "node:test";
import assert from "node:assert/strict";
import { createServer, type Server } from "node:http";
import { SpatialClient } from "../src/client.ts";
import { SpatialApiError } from "../src/errors.ts";
import { decodeFeatureBatch } from "../src/feature-batch.ts";

/**
 * A tiny scriptable HTTP server that behaves like the spatial host for the
 * SDK's client tests: URL assertions, the camelCase wire contract, batch
 * decoding and structured error mapping — no .NET needed.
 */
function fakeHost(routes: Record<string, (req: import("node:http").IncomingMessage) => { status: number; body: string | Uint8Array; contentType: string; headers?: Record<string, string> }>): Promise<{ url: string; server: Server; requests: string[] }> {
  const requests: string[] = [];
  const server = createServer((req, res) => {
    const url = new URL(req.url ?? "/", "http://localhost");
    requests.push(`${req.method} ${url.pathname}`);
    const route = routes[url.pathname];
    if (!route) {
      res.writeHead(404, { "content-type": "application/json" });
      res.end(JSON.stringify({ code: "not.found", message: "no such route" }));
      return;
    }
    const answer = route(req);
    res.writeHead(answer.status, { "content-type": answer.contentType, ...(answer.headers ?? {}) });
    res.end(answer.body);
  });
  return new Promise((resolve) => {
    server.listen(0, "127.0.0.1", () => {
      const address = server.address();
      resolve({ url: `http://127.0.0.1:${typeof address === "object" && address ? address.port : 0}`, server, requests });
    });
  });
}

test("geometry methods post SGEOM and decode the result", async (t) => {
  const buffered = pointBuffer();
  const host = await fakeHost({
    "/api/geometry/buffer": () => ({
      status: 200,
      body: JSON.stringify({ geometry: toBase64(buffered) }),
      contentType: "application/json",
    }),
    "/api/geometry/validate": () => ({ status: 200, body: JSON.stringify({ valid: true }), contentType: "application/json" }),
  });
  t.after(() => host.server.close());
  const client = new SpatialClient(host.url);

  const result = await client.buffer(new Uint8Array([1, 2, 3]), 1.5);
  assert.deepEqual(result, buffered);

  assert.equal(await client.validate(new Uint8Array([9])), true);

  assert.deepEqual(host.requests, ["POST /api/geometry/buffer", "POST /api/geometry/validate"]);
});

test("describe and transform hit the typed routes", async (t) => {
  const host = await fakeHost({
    "/api/crs/describe": () => ({
      status: 200,
      body: JSON.stringify({ authority: "EPSG", code: "4326", name: "WGS 84", kind: "geographic", dimension: 2, axes: [], datum: null, ellipsoid: null }),
      contentType: "application/json",
    }),
    "/api/coordinates/transform": () => ({
      status: 200,
      body: JSON.stringify({ geometry: toBase64(new Uint8Array([4, 5])) }),
      contentType: "application/json",
    }),
  });
  t.after(() => host.server.close());
  const client = new SpatialClient(host.url);

  const description = await client.describeCrs("EPSG:4326");
  assert.equal(description.code, "4326");

  const transformed = await client.transform(new Uint8Array([1]), "EPSG:32632");
  assert.deepEqual(transformed, new Uint8Array([4, 5]));
});

test("catalogue scan and query decode batches", async (t) => {
  const sfbat = buildSfbat();
  const host = await fakeHost({
    "/api/catalogue": () => ({
      status: 200,
      body: JSON.stringify({ datasets: [{ id: "demo.points", schema: "demo", table: "points", geometryColumn: "geometry", srid: 4326, estimatedRowCount: 110 }] }),
      contentType: "application/json",
    }),
    "/api/features/scan": () => ({
      status: 200,
      body: JSON.stringify({ batches: [toBase64(sfbat)] }),
      contentType: "application/json",
    }),
  });
  t.after(() => host.server.close());
  const client = new SpatialClient(host.url);

  const catalogue = await client.catalogue();
  assert.equal(catalogue.datasets[0]?.id, "demo.points");

  const batches = await client.scan("demo.points");
  assert.equal(batches.length, 1);
  assert.equal(batches[0]?.features[0]?.id, "f1");
  assert.deepEqual(decodeFeatureBatch(sfbat).features[0]?.id, "f1");
});

test("host errors throw SpatialApiError with the structured code", async (t) => {
  const host = await fakeHost({
    "/api/geometry/buffer": () => ({
      status: 400,
      body: JSON.stringify({ code: "invalid.arguments", message: "bad distance" }),
      contentType: "application/json",
    }),
  });
  t.after(() => host.server.close());
  const client = new SpatialClient(host.url);

  await assert.rejects(() => client.buffer(new Uint8Array([1]), Number.NaN), (error: unknown) => {
    assert.ok(error instanceof SpatialApiError);
    assert.equal((error as SpatialApiError).status, 400);
    assert.equal((error as SpatialApiError).code, "invalid.arguments");
    return true;
  });
});

test("transactions and sleep round trip", async (t) => {
  const host = await fakeHost({
    "/api/transactions/begin": () => ({ status: 200, body: JSON.stringify({ transaction: "abc" }), contentType: "application/json" }),
    "/api/transactions/commit": () => ({ status: 200, body: JSON.stringify({ ok: true }), contentType: "application/json" }),
    "/api/demo/sleep": () => ({ status: 200, body: JSON.stringify({ slept: 50 }), contentType: "application/json" }),
  });
  t.after(() => host.server.close());
  const client = new SpatialClient(host.url);

  assert.equal(await client.beginTransaction(), "abc");
  assert.equal(await client.commitTransaction("abc"), true);
  assert.equal(await client.sleep(50), 50);
});

/** A stand-in buffered result (opaque bytes — the client never interprets geometry). */
function pointBuffer(): Uint8Array {
  return new Uint8Array([7, 7, 7]);
}

function toBase64(bytes: Uint8Array): string {
  return Buffer.from(bytes).toString("base64");
}

/** Builds the SFBAT v1 bytes for {"name": String} with one feature "f1" name="alice". */
function buildSfbat(): Uint8Array {
  const textEncoder = new TextEncoder();
  const parts: Uint8Array[] = [];
  const push = (bytes: Uint8Array) => parts.push(bytes);
  const pushString = (value: string) => {
    const bytes = textEncoder.encode(value);
    push(i32(bytes.length));
    push(bytes);
  };
  push(new TextEncoder().encode("SFBAT"));
  push(Uint8Array.of(1));
  push(i32(1));
  pushString("name");
  push(Uint8Array.of(4 /* String */, 1 /* nullable */, 0));
  push(i32(1));
  pushString("f1");
  push(Uint8Array.of(0));
  pushString("alice");
  return concat(parts);
}

function i32(value: number): Uint8Array {
  const bytes = new Uint8Array(4);
  new DataView(bytes.buffer).setInt32(0, value, true);
  return bytes;
}

function concat(chunks: Uint8Array[]): Uint8Array {
  const total = chunks.reduce((sum, chunk) => sum + chunk.length, 0);
  const result = new Uint8Array(total);
  let offset = 0;
  for (const chunk of chunks) {
    result.set(chunk, offset);
    offset += chunk.length;
  }
  return result;
}

test("render returns image bytes and metadata headers", async (t) => {
  const host = await fakeHost({
    "/api/render": () => ({
      status: 200,
      body: Uint8Array.of(0x89, 0x50, 0x4e, 0x47),
      contentType: "image/png",
      headers: { "x-raster-width": "400", "x-raster-height": "250" },
    }),
    "/api/render/capabilities": () => ({
      status: 200,
      body: JSON.stringify({ formats: ["png"], pixelFormats: ["rgba8888"], blendModes: ["over"], maxPixels: 16777216, imagerySources: [] }),
      contentType: "application/json",
    }),
  });
  t.after(() => host.server.close());
  const client = new SpatialClient(host.url);

  const image = await client.render({
    viewport: { minX: -10, minY: 35, maxX: 30, maxY: 60, width: 400, height: 250, crs: "EPSG:4326" },
    style: { version: 8, layers: [] },
    layers: [{ dataset: "demo.cities", store: "demo" }],
    format: "png",
  });

  assert.equal(image.mediaType, "image/png");
  assert.equal(image.format, "png");
  assert.equal(image.width, 400);
  assert.equal(image.height, 250);
  assert.deepEqual([...image.bytes], [0x89, 0x50, 0x4e, 0x47]);

  const capabilities = await client.renderCapabilities();
  assert.deepEqual(capabilities.formats, ["png"]);
  assert.deepEqual(host.requests, ["POST /api/render", "GET /api/render/capabilities"]);
});

test("rendering a map posts to its render route and returns image bytes", async (t) => {
  const host = await fakeHost({
    "/api/maps/cities/render": () => ({
      status: 200,
      body: Uint8Array.of(0x89, 0x50, 0x4e, 0x47),
      contentType: "image/png",
      headers: { "x-raster-width": "400", "x-raster-height": "250" },
    }),
  });
  t.after(() => host.server.close());
  const client = new SpatialClient(host.url);

  const image = await client.renderMap("cities", {
    viewport: { minX: -10, minY: 35, maxX: 30, maxY: 60, width: 400, height: 250, crs: "EPSG:4326" },
    format: "png",
  });

  assert.equal(image.mediaType, "image/png");
  assert.equal(image.format, "png");
  assert.equal(image.width, 400);
  assert.deepEqual([...image.bytes], [0x89, 0x50, 0x4e, 0x47]);
  assert.deepEqual(host.requests, ["POST /api/maps/cities/render"]);
});

test("render maps host failures to SpatialApiError", async (t) => {
  const host = await fakeHost({
    "/api/render": () => ({
      status: 400,
      body: JSON.stringify({ code: "invalid.arguments", message: "bad format" }),
      contentType: "application/json",
    }),
  });
  t.after(() => host.server.close());
  const client = new SpatialClient(host.url);

  await assert.rejects(
    () => client.render({ viewport: { minX: 0, minY: 0, maxX: 1, maxY: 1, width: 10, height: 10, crs: "EPSG:4326" }, style: {}, layers: [] }),
    (error: unknown) => error instanceof SpatialApiError && error.status === 400 && error.code === "invalid.arguments",
  );
});

test("tiles render, batch and describe the scheme", async (t) => {
  const host = await fakeHost({
    "/api/render/tiles/3/1/2.png": () => ({
      status: 200,
      body: Uint8Array.of(0x89, 0x50, 0x4e, 0x47),
      contentType: "image/png",
      headers: { "x-raster-width": "256", "x-raster-height": "256" },
    }),
    "/api/render/tiles/batch": () => ({
      status: 200,
      body: JSON.stringify({ tiles: [{ z: 0, x: 0, y: 0, cached: false, contentType: "image/png", width: 256, height: 256, content: "AQID" }] }),
      contentType: "application/json",
    }),
    "/api/render/tiles/capabilities": () => ({
      status: 200,
      body: JSON.stringify({
        defaultScheme: "webmercator",
        maxTilesPerBatch: 64,
        schemes: [{ id: "webmercator", crs: "EPSG:3857", tileSize: 256, minZoom: 0, maxZoom: 23, levels: [{ zoom: 0, resolution: 1, scaleDenominator: 2 }] }],
      }),
      contentType: "application/json",
    }),
  });
  t.after(() => host.server.close());
  const client = new SpatialClient(host.url);
  const request = { style: { version: 8, layers: [] }, layers: [{ dataset: "demo.cities", store: "demo" }] };

  const image = await client.renderTile(3, 1, 2, request);
  assert.equal(image.mediaType, "image/png");
  assert.equal(image.width, 256);
  assert.equal(image.height, 256);

  const batch = await client.renderTiles({ request, tiles: [{ z: 0, x: 0, y: 0 }] });
  assert.equal(batch.tiles[0]?.cached, false);
  assert.equal(Buffer.from(batch.tiles[0]?.content ?? "", "base64").toString("hex"), "010203");

  const capabilities = await client.tileCapabilities();
  assert.equal(capabilities.defaultScheme, "webmercator");
  assert.equal(capabilities.maxTilesPerBatch, 64);
  assert.deepEqual(
    host.requests,
    ["POST /api/render/tiles/3/1/2.png", "POST /api/render/tiles/batch", "GET /api/render/tiles/capabilities"],
  );
});

test("maps and ingest hit the admin routes", async (t) => {
  const map = { name: "parks", store: "memory", services: ["feature" as const], layers: [{ dataset: "public.parks", layerId: 0, kind: "feature" as const }] };
  const host = await fakeHost({
    "/api/maps": () => ({
      status: 200,
      body: JSON.stringify([map]),
      contentType: "application/json",
    }),
    "/api/maps/parks": (req) =>
      req.method === "DELETE"
        ? { status: 200, body: JSON.stringify(true), contentType: "application/json" }
        : { status: 200, body: JSON.stringify(map), contentType: "application/json" },
    "/api/ingest": () => ({
      status: 200,
      body: JSON.stringify({ dataset: "public.parks", features: 2, srid: 4326, identityField: "id", map }),
      contentType: "application/json",
    }),
  });
  t.after(() => host.server.close());
  const client = new SpatialClient(host.url);

  assert.equal((await client.listMaps()).length, 1);
  assert.equal((await client.getMap("parks")).name, "parks");
  assert.equal((await client.putMap(map, "secret")).name, "parks");
  assert.equal(await client.deleteMap("parks", "secret"), true);

  const result = await client.ingest(
    new Blob(["{}"], { type: "application/geo+json" }),
    "parks.geojson",
    { dataset: "public.parks", srid: 4326, publish: "parks" },
    "secret",
  );
  assert.equal(result.features, 2);
  assert.equal(result.map?.name, "parks");

  assert.deepEqual(host.requests, [
    "GET /api/maps",
    "GET /api/maps/parks",
    "PUT /api/maps/parks",
    "DELETE /api/maps/parks",
    "POST /api/ingest",
  ]);
});
