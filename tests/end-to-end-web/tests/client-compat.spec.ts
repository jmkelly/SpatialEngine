import { test as base, expect, chromium, request as playwrightRequest, type Page } from "@playwright/test";
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { inflateSync } from "node:zlib";

// Client-compat proof (T-068): genuine OpenLayers + Leaflet clients render
// the host's services in a real browser — the companion to the MapLibre
// workbench e2e and the QgisReplayTests HTTP replays. OL pins the WMS 1.3.0
// (CRS) dialect, Leaflet pins 1.1.1 (SRS + LatLonBoundingBox).
//
// The proof pages are static scaffolding served by the Playwright webServer
// (proof-server.mjs, same origin, reverse-proxying /ogc + /api to the host);
// the qgis map below mirrors tools/seed/qgis-map.mjs (demo cities plus the
// fixture's memory routes/zones) with the wms+wfs+tiles services the pages
// need. No screenshot goldens: non-blank rendering is asserted by pixel
// variance, never byte equality.

// A fresh browser per test: headless rendering degrades after several
// contexts in one Chromium process (same isolation as workbench.spec.ts).
export const test = base.extend({
  browser: async ({}, use) => {
    const browser = await chromium.launch({ args: ["--disable-dev-shm-usage", "--disable-gpu"] });
    await use(browser);
    await browser.close();
  },
});

const host = process.env.WORKBENCH_URL ?? "http://127.0.0.1:5999";
const proofBase = `http://127.0.0.1:${process.env.PROOF_PORT ?? "5898"}`;
// The admin token eng/workbench-e2e.sh starts the host with.
const adminToken = "workbench-e2e-token";

const here = dirname(fileURLToPath(import.meta.url));
const fixture = JSON.parse(
  readFileSync(resolve(here, "../../../tests/fixtures/qgis/qgis-4.2.2-wms.json"), "utf8"),
) as {
  seedDatasets: { store: string; dataset: string; srid: number; format: string; body: string }[];
  mapLayers: { dataset: string; store?: string; name: string; style: string }[];
};

test.beforeAll(async () => {
  const api = await playwrightRequest.newContext({ baseURL: host });
  try {
    // The memory routes/zones from the shared QGIS fixture (demo.cities is
    // always present); a 409 means a previous run already loaded them.
    for (const seed of fixture.seedDatasets) {
      const ingest = await api.post(
        `/api/ingest?store=${seed.store}&dataset=${seed.dataset}&srid=${seed.srid}&format=${seed.format}`,
        {
          headers: { authorization: `Bearer ${adminToken}`, "content-type": "application/geo+json" },
          data: seed.body,
        },
      );
      // A rerun against the same host reuses the datasets (the host answers
      // 400 invalid.arguments "already exists").
      const ingestBody = await ingest.text();
      expect(
        [200, 201].includes(ingest.status()) || ingestBody.includes("already exists"),
        `ingest ${seed.dataset} -> ${ingest.status()} ${ingestBody}`,
      ).toBe(true);
    }
    const put = await api.put("/api/maps/qgis", {
      headers: { authorization: `Bearer ${adminToken}` },
      data: {
        name: "qgis",
        store: "demo",
        services: ["wms", "wfs", "tiles"],
        description: "QGIS client-compat map: demo cities plus memory routes and zones.",
        layers: fixture.mapLayers.map((layer) => ({
          dataset: layer.dataset,
          layerId: -1,
          name: layer.name,
          ...(layer.store ? { store: layer.store } : {}),
          style: layer.style,
        })),
      },
    });
    expect([200, 201].includes(put.status()), `publish qgis -> ${put.status()} ${await put.text()}`).toBe(true);
  } finally {
    await api.dispose();
  }
});

interface ProofState {
  ready: boolean;
  wmsTiles: number;
  xyzTiles: number;
  vectorCount: number | null;
  gfi: string | null;
  errors: string[];
}

async function openProof(page: Page, name: "ol-proof.html" | "leaflet-proof.html") {
  const consoleErrors: string[] = [];
  const failedRequests: string[] = [];
  page.on("console", (message) => {
    if (message.type() === "error") consoleErrors.push(message.text().slice(0, 300));
  });
  page.on("pageerror", (error) => consoleErrors.push(String(error).slice(0, 300)));
  page.on("requestfailed", (req) => failedRequests.push(`${req.method()} ${req.url().slice(0, 200)}`));
  await page.goto(`${proofBase}/${name}`, { waitUntil: "load" });
  await page.waitForFunction(() => (window as unknown as { __proof?: { ready: boolean } }).__proof?.ready === true, {
    polling: 500,
    timeout: 60_000,
  });
  const state = await page.evaluate(() => (window as unknown as { __proof: ProofState }).__proof);
  expect(state.errors, "proof page errors").toEqual([]);
  expect(consoleErrors, "console errors").toEqual([]);
  expect(failedRequests, "failed requests").toEqual([]);
  return state;
}

/** Luminance variance of a PNG screenshot: non-blank without goldens. */
function pixelStats(png: Buffer): { width: number; height: number; mean: number; variance: number } {
  let offset = 8;
  let width = 0;
  let height = 0;
  let bitDepth = 0;
  let colorType = 0;
  const idat: Buffer[] = [];
  while (offset < png.length) {
    const length = png.readUInt32BE(offset);
    const type = png.toString("ascii", offset + 4, offset + 8);
    const data = png.subarray(offset + 8, offset + 8 + length);
    if (type === "IHDR") {
      width = data.readUInt32BE(0);
      height = data.readUInt32BE(4);
      bitDepth = data[8];
      colorType = data[9];
    } else if (type === "IDAT") {
      idat.push(Buffer.from(data));
    }
    offset += 12 + length;
  }
  expect(bitDepth).toBe(8);
  expect([2, 6].includes(colorType)).toBe(true);
  const channels = colorType === 6 ? 4 : 3;
  const raw = inflateSync(Buffer.concat(idat));
  const stride = width * channels;
  let sum = 0;
  let sumSq = 0;
  let count = 0;
  let previous = Buffer.alloc(stride);
  let position = 0;
  for (let row = 0; row < height; row++) {
    const filter = raw[position++];
    const current = raw.subarray(position, position + stride);
    position += stride;
    const recon = Buffer.alloc(stride);
    for (let i = 0; i < stride; i++) {
      const a = i >= channels ? recon[i - channels] : 0;
      const b = previous[i];
      const c = i >= channels ? previous[i - channels] : 0;
      let value = current[i];
      if (filter === 1) value += a;
      else if (filter === 2) value += b;
      else if (filter === 3) value += (a + b) >> 1;
      else if (filter === 4) value += paeth(a, b, c);
      recon[i] = value & 0xff;
    }
    previous = recon;
    for (let x = 0; x < width; x += 2) {
      const luminance = (recon[x * channels] + recon[x * channels + 1] + recon[x * channels + 2]) / 3;
      sum += luminance;
      sumSq += luminance * luminance;
      count++;
    }
  }
  const mean = sum / count;
  return { width, height, mean, variance: sumSq / count - mean * mean };
}

function paeth(a: number, b: number, c: number): number {
  const p = a + b - c;
  const pa = Math.abs(p - a);
  const pb = Math.abs(p - b);
  const pc = Math.abs(p - c);
  return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
}

const pngMagic = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];

test("WMS 1.3.0 and 1.1.1 dialects answer GetCapabilities, GetMap and GetFeatureInfo", async ({ request }) => {
  // No VERSION (what QGIS sends on add-layer) serves the 1.3.0 dialect.
  const caps = await request.get("/ogc/qgis/wms?SERVICE=WMS&REQUEST=GetCapabilities");
  expect(caps.status()).toBe(200);
  const capsBody = await caps.text();
  expect(capsBody).toContain("WMS_Capabilities");
  for (const layer of ["cities", "routes", "zones"]) expect(capsBody).toContain(layer);

  // The 1.1.1 dialect speaks SRS + LatLonBoundingBox.
  const caps111 = await request.get("/ogc/qgis/wms?SERVICE=WMS&VERSION=1.1.1&REQUEST=GetCapabilities");
  expect(caps111.status()).toBe(200);
  const caps111Body = await caps111.text();
  expect(caps111Body).toContain("LatLonBoundingBox");
  expect(caps111Body).not.toContain("ServiceException");

  // GetMap 1.3.0 with the CRS axis order (the QGIS replay's zoom query).
  const map130 = await request.get(
    "/ogc/qgis/wms?SERVICE=WMS&VERSION=1.3.0&REQUEST=GetMap&BBOX=50,0,59.9139,10&CRS=EPSG:4326&WIDTH=400&HEIGHT=397&LAYERS=cities,routes,zones&STYLES=,,&FORMAT=image/png&TRANSPARENT=TRUE",
  );
  expect(map130.status()).toBe(200);
  expect(map130.headers()["content-type"]).toContain("image/png");
  expect(Array.from((await map130.body()).subarray(0, 8))).toEqual(pngMagic);

  // GetMap 1.1.1 with SRS and lon/lat order.
  const map111 = await request.get(
    "/ogc/qgis/wms?SERVICE=WMS&VERSION=1.1.1&REQUEST=GetMap&BBOX=0,50,10,59.9139&SRS=EPSG:4326&WIDTH=400&HEIGHT=397&LAYERS=cities,routes,zones&STYLES=,,&FORMAT=image/png&TRANSPARENT=TRUE",
  );
  expect(map111.status()).toBe(200);
  expect(map111.headers()["content-type"]).toContain("image/png");
  expect(Array.from((await map111.body()).subarray(0, 8))).toEqual(pngMagic);

  // GetFeatureInfo 1.3.0 (I/J) identifies Amsterdam; 1.1.1 (X/Y) matches it.
  const gfi130 = await request.get(
    "/ogc/qgis/wms?SERVICE=WMS&VERSION=1.3.0&REQUEST=GetFeatureInfo&BBOX=35,-10,60,30&CRS=EPSG:4326&WIDTH=800&HEIGHT=600&LAYERS=cities&STYLES=&FORMAT=image/png&QUERY_LAYERS=cities&INFO_FORMAT=text/html&I=298&J=183&FEATURE_COUNT=10",
  );
  expect(gfi130.status()).toBe(200);
  expect(await gfi130.text()).toContain("Amsterdam");

  const gfi111 = await request.get(
    "/ogc/qgis/wms?SERVICE=WMS&VERSION=1.1.1&REQUEST=GetFeatureInfo&BBOX=-10,35,30,60&SRS=EPSG:4326&WIDTH=800&HEIGHT=600&LAYERS=cities&STYLES=&FORMAT=image/png&QUERY_LAYERS=cities&INFO_FORMAT=text/html&X=298&Y=183&FEATURE_COUNT=10",
  );
  expect(gfi111.status()).toBe(200);
  expect(await gfi111.text()).toContain("Amsterdam");
});

test("WFS GetFeature pages GeoJSON with srsName reprojection, XYZ tiles render PNG", async ({ request }) => {
  const page1 = await request.get(
    "/ogc/qgis/wfs?service=WFS&request=GetFeature&typeNames=cities&outputFormat=application%2Fgeo%2Bjson&count=5&startIndex=0",
  );
  expect(page1.status()).toBe(200);
  const page1Body = await page1.json();
  expect(page1Body.type).toBe("FeatureCollection");
  expect(page1Body.numberReturned).toBe(5);
  expect(page1Body.features.length).toBe(5);
  const matched: number = page1Body.numberMatched;
  expect(matched).toBeGreaterThan(5);

  const page2 = await request.get(
    "/ogc/qgis/wfs?service=WFS&request=GetFeature&typeNames=cities&outputFormat=application%2Fgeo%2Bjson&count=5&startIndex=5",
  );
  const page2Body = await page2.json();
  expect(page2Body.numberMatched).toBe(matched);
  expect(page2Body.features[0].id).not.toBe(page1Body.features[0].id);

  // Paging across all three layers serves the whole collection: 5 + 7.
  const all = await request.get(
    "/ogc/qgis/wfs?service=WFS&request=GetFeature&typeNames=cities%2Croutes%2Czones&outputFormat=application%2Fgeo%2Bjson&count=5&startIndex=0",
  );
  const allBody = await all.json();
  expect(allBody.numberMatched).toBe(12);
  expect(allBody.numberReturned).toBe(5);
  const rest = await request.get(
    "/ogc/qgis/wfs?service=WFS&request=GetFeature&typeNames=cities%2Croutes%2Czones&outputFormat=application%2Fgeo%2Bjson&count=50&startIndex=5",
  );
  expect((await rest.json()).numberReturned).toBe(7);

  // srsName reprojects to Web-Mercator metres.
  const reprojected = await request.get(
    "/ogc/qgis/wfs?service=WFS&request=GetFeature&typeNames=cities&outputFormat=application%2Fgeo%2Bjson&count=1&srsName=EPSG:3857",
  );
  expect(reprojected.status()).toBe(200);
  const [x, y] = (await reprojected.json()).features[0].geometry.coordinates;
  expect(Math.abs(x)).toBeGreaterThan(1000);
  expect(Math.abs(y)).toBeGreaterThan(1000);

  const tile = await request.get("/api/maps/qgis/tiles/4/8/5.png");
  expect(tile.status()).toBe(200);
  expect(tile.headers()["content-type"]).toContain("image/png");
  expect(Array.from((await tile.body()).subarray(0, 8))).toEqual(pngMagic);
});

test("OpenLayers renders WMS 1.3.0 + XYZ + WFS vector with a non-blank canvas", async ({ page }) => {
  const state = await openProof(page, "ol-proof.html");
  expect(state.wmsTiles).toBeGreaterThan(0);
  expect(state.xyzTiles).toBeGreaterThan(0);
  expect(state.vectorCount).not.toBeNull();
  // Honest count: the server pages all 12 features (asserted above), but
  // the memory routes/zones reuse ids 1,2 across layers and OpenLayers
  // documents that a same-id feature is not added — so the genuine client
  // holds 10 (follow-up T-084 tracks the id scoping).
  expect(state.vectorCount as number).toBe(10);
  expect(state.gfi).not.toBeNull();
  expect(state.gfi as string).toContain("Amsterdam");

  const shot = await page.locator("#map").screenshot();
  const stats = pixelStats(shot);
  expect(stats.variance).toBeGreaterThan(100);
});

test("Leaflet renders WMS 1.1.1 + XYZ with a non-blank canvas", async ({ page }) => {
  const state = await openProof(page, "leaflet-proof.html");
  expect(state.wmsTiles).toBeGreaterThan(0);
  expect(state.xyzTiles).toBeGreaterThan(0);
  expect(state.gfi).not.toBeNull();
  expect(state.gfi as string).toContain("Amsterdam");

  const shot = await page.locator("#map").screenshot();
  const stats = pixelStats(shot);
  expect(stats.variance).toBeGreaterThan(100);
});
