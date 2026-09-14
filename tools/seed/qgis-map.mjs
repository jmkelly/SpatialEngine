#!/usr/bin/env node
// Seeds the `qgis` conformance map: the demo points plus the memory
// line/polygon datasets, published as a WMS map for visual parity work.
//
// The datasets and layers come from `tests/fixtures/qgis/qgis-4.2.2-wms.json`
// — the same fixture `QgisReplayTests` replays verbatim — so the replay
// suite, this seed, and the later conformance slices (parity page, chaos
// toggles) and T-068 all share one definition of the map.
//
// Like `seed.mjs` it is a pure client of the public admin API: ingest through
// POST /api/ingest, publish through PUT /api/maps/{name}. No geometry or
// style logic lives here; the styles are the fixture's verbatim fragments.
//
// Usage:
//   node tools/seed/qgis-map.mjs [--host=URL] [--token=TOKEN] [--force]
//                                [--dry-run]
//
// Prefer `eng/conformance.sh`, which also starts a host when none is running.

import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const fixturePath = join(here, "..", "..", "tests", "fixtures", "qgis", "qgis-4.2.2-wms.json");
const fixture = JSON.parse(readFileSync(fixturePath, "utf8"));

const MapName = "qgis";
const MapStore = "demo";
const MapServices = ["wms"];

const options = parseArgs(process.argv.slice(2));
const host = (options.host ?? process.env.SPATIAL_SEED_HOST ?? "http://127.0.0.1:5251").replace(/\/$/, "");
const token = options.token ?? process.env.SPATIAL_ADMIN_TOKEN ?? "conformance-admin-token";
const force = options.force === true;
const dryRun = options["dry-run"] === true;

const failures = [];
let ingested = 0;
let reused = 0;

await assertReachable();

if (!dryRun) {
  for (const dataset of fixture.seedDatasets) {
    try {
      await seedDataset(dataset);
    } catch (error) {
      failures.push(`${dataset.dataset}: ${error.message}`);
      console.error(`  ✗ ${dataset.dataset}: ${error.message}`);
    }
  }

  try {
    await publishMap();
  } catch (error) {
    failures.push(`${MapName}: ${error.message}`);
    console.error(`  ✗ ${MapName}: ${error.message}`);
  }

  if (failures.length === 0) {
    await verifyMap();
  }
} else {
  console.log(
    `Would ingest ${fixture.seedDatasets.length} dataset(s) and publish the ${MapName} map against ${host}.`,
  );
}

summarise();
process.exit(failures.length === 0 ? 0 : 1);

// ---- datasets ---------------------------------------------------------------

async function seedDataset(dataset) {
  if (!force && (await datasetExists(dataset))) {
    reused++;
    console.log(`• ${dataset.dataset} — already present, reusing`);
    return;
  }

  console.log(`• ${dataset.dataset} — ingesting ${dataset.body.length} byte(s) of fixture GeoJSON`);
  const query = new URLSearchParams({
    store: dataset.store,
    dataset: dataset.dataset,
    srid: String(dataset.srid),
    format: dataset.format,
  });
  const response = await request(`/api/ingest?${query.toString()}`, {
    method: "POST",
    body: dataset.body,
    headers: { "Content-Type": "application/geo+json" },
  });
  if (!response.ok) {
    throw new Error(await message(response));
  }

  ingested++;
  console.log(`  ✓ loaded ${response.body.features.toLocaleString()} feature(s) into ${dataset.store}/${dataset.dataset}`);
}

async function datasetExists(dataset) {
  const response = await request(
    `/api/datasets/${encodeURIComponent(dataset.dataset)}?store=${encodeURIComponent(dataset.store)}`,
  );
  return response.status === 200;
}

// ---- map --------------------------------------------------------------------

async function publishMap() {
  const existing = await getMap(MapName);
  const existingIds = new Map((existing?.layers ?? []).map((layer) => [layer.dataset, layer.layerId]));

  const payload = {
    name: MapName,
    store: MapStore,
    services: MapServices,
    description: "QGIS conformance map: demo cities plus memory routes and zones (visual parity reference).",
    layers: fixture.mapLayers.map((layer) => ({
      dataset: layer.dataset,
      // Preserve a published id so re-running never renumbers the layers
      // (ADR-0041); a new layer asks the registry to assign the next free id.
      layerId: existingIds.get(layer.dataset) ?? -1,
      name: layer.name,
      ...(layer.store ? { store: layer.store } : {}),
      style: layer.style,
    })),
  };

  const response = await request(`/api/maps/${encodeURIComponent(MapName)}`, {
    method: "PUT",
    json: payload,
  });
  if (!response.ok) {
    throw new Error(await message(response));
  }

  console.log(`  ✓ published map ${MapName} [${MapServices.join(", ")}] (${payload.layers.length} layer(s))`);
}

async function getMap(name) {
  const response = await request(`/api/maps/${encodeURIComponent(name)}`);
  return response.status === 200 ? response.body : null;
}

async function verifyMap() {
  console.log("• verifying the qgis WMS");
  // No VERSION: mirrors the QGIS add-layer GetCapabilities the replays pin.
  const response = await request(
    `/ogc/${MapName}/wms?SERVICE=WMS&REQUEST=GetCapabilities`,
  );
  if (!response.ok) {
    failures.push(`${MapName}: GetCapabilities returned ${response.status}`);
    console.error(`  ✗ ${MapName}/wms GetCapabilities — ${response.status}`);
    return;
  }

  console.log(`  ✓ ${MapName}/wms GetCapabilities answers`);
}

// ---- plumbing ---------------------------------------------------------------

async function assertReachable() {
  try {
    const response = await fetch(`${host}/health/ready`);
    if (!response.ok) {
      throw new Error(`health returned ${response.status}`);
    }
  } catch (error) {
    console.error(`The host at ${host} is not reachable (${error.message}).`);
    console.error("Start one with an admin token, for example:");
    console.error(`  SPATIAL_ADMIN_TOKEN=${token} dotnet run --project src/Spatial.Host --urls ${host}`);
    process.exit(1);
  }
}

async function request(path, init = {}) {
  const headers = { Authorization: `Bearer ${token}`, ...(init.headers ?? {}) };
  if (init.json !== undefined) {
    headers["Content-Type"] = "application/json";
  }

  const response = await fetch(`${host}${path}`, {
    method: init.method ?? "GET",
    headers,
    body: init.json !== undefined ? JSON.stringify(init.json) : init.body,
  });
  const text = await response.text();
  let body = null;
  try {
    body = text.length > 0 ? JSON.parse(text) : null;
  } catch {
    body = text;
  }

  return { ok: response.ok, status: response.status, body };
}

async function message(response) {
  const body = response.body;
  if (body && typeof body === "object" && body.message) {
    return `${response.status} ${body.message}`;
  }

  return `${response.status} ${JSON.stringify(body)}`;
}

function summarise() {
  console.log("");
  console.log(`QGIS map ${failures.length === 0 ? "ready" : "finished with errors"} against ${host}:`);
  console.log(`  datasets: ${ingested} loaded, ${reused} reused`);
  if (failures.length > 0) {
    console.log(`  failures: ${failures.length}`);
    for (const failure of failures) {
      console.log(`    - ${failure}`);
    }
  }

  console.log("");
  console.log(`  WMS capabilities:  ${host}/ogc/${MapName}/wms?SERVICE=WMS&REQUEST=GetCapabilities`);
}

function parseArgs(argv) {
  const parsed = {};
  for (const arg of argv) {
    if (!arg.startsWith("--")) continue;
    const [key, value] = arg.slice(2).split("=", 2);
    parsed[key] = value === undefined ? true : value;
  }

  return parsed;
}
