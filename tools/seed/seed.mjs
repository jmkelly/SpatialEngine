#!/usr/bin/env node
// Seeds a running Spatial Engine host with realistic, non-trivial spatial
// data and a set of styled feature/map services.
//
// It is a pure client of the public admin API (ADR-0041): it fetches real
// data from the internet, ingests it through POST /api/ingest (using the
// engine's server-side reprojection when a source declares one), and
// publishes the maps through PUT /api/maps/{name}. The only style logic here
// lowers the manifest's compact draw recipe to the persisted MapLibre
// fragment the host stores (ADR-0047); no geometry logic lives here.
//
// Usage:
//   node tools/seed/seed.mjs [--host=URL] [--token=TOKEN] [--store=memory]
//                            [--only=WorldReference,SeismicActivity]
//                            [--force] [--no-verify] [--list] [--dry-run]
//
// Prefer `eng/seed.sh`, which also starts a host when none is running.

import { sources, services } from "./manifest.mjs";

// Defaults for a manifest layer's compact draw recipe, merged before it is
// lowered to the persisted MapLibre fragment (ADR-0047).
const styleDefaults = { color: "#4fc3f7", opacity: 1, lineWidth: 2, radius: 5, visible: true };

const options = parseArgs(process.argv.slice(2));
const host = (options.host ?? process.env.SPATIAL_SEED_HOST ?? "http://127.0.0.1:5201").replace(/\/$/, "");
const token = options.token ?? process.env.SPATIAL_ADMIN_TOKEN ?? "seed-admin-token";
const store = options.store ?? process.env.SPATIAL_SEED_STORE ?? "memory";
const only = options.only ? new Set(String(options.only).split(",").map((value) => value.trim()).filter(Boolean)) : null;
const verify = options.verify !== false;
const dryRun = options["dry-run"] === true;
const force = options.force === true;

if (options.list) {
  listManifest();
  process.exit(0);
}

const downloadCache = new Map();
const failures = [];
let ingested = 0;
let reused = 0;
let published = 0;

await assertReachable();

const selectedSources = sources.filter((source) => matches(only, source.id));
const selectedServices = services.filter((service) => matches(only, service.name));

if (!dryRun) {
  for (const source of selectedSources) {
    try {
      await seedSource(source);
    } catch (error) {
      failures.push(`${source.id}: ${error.message}`);
      console.error(`  ✗ ${source.id}: ${error.message}`);
    }
  }

  for (const service of selectedServices) {
    try {
      await seedService(service);
    } catch (error) {
      failures.push(`${service.name}: ${error.message}`);
      console.error(`  ✗ ${service.name}: ${error.message}`);
    }
  }

  if (verify) {
    await verifyServices(selectedServices);
  }
} else {
  console.log(`Would ingest ${selectedSources.length} dataset(s) and publish ${selectedServices.length} service(s) against ${host} (${store}).`);
}

summarise();
process.exit(failures.length === 0 ? 0 : 1);

// ---- sources -----------------------------------------------------------------

async function seedSource(source) {
  if (matches(only, source.id) === false) return;
  if (source.sourceSrid && source.sourceSrid === source.srid) {
    delete source.sourceSrid;
  }

  if (!force && (await datasetExists(source.id))) {
    reused++;
    console.log(`• ${source.id} — already present, reusing`);
    return;
  }

  console.log(`• ${source.id} — fetching ${source.note ?? source.url}`);
  const bytes = await download(source.url);
  const query = new URLSearchParams({
    store,
    dataset: source.id,
    srid: String(source.srid),
    format: source.format,
  });
  if (source.identity) query.set("identity", source.identity);
  if (source.identityField) query.set("identityField", source.identityField);
  if (source.sourceSrid) query.set("sourceSrid", String(source.sourceSrid));

  const response = await request(`/api/ingest?${query.toString()}`, {
    method: "POST",
    body: bytes,
    headers: contentType(source.format),
  });
  if (!response.ok) {
    throw new Error(await message(response));
  }

  ingested++;
  console.log(`  ✓ loaded ${response.body.features.toLocaleString()} feature(s) into ${store}/${source.id}`);
}

async function datasetExists(dataset) {
  const response = await request(`/api/datasets/${encodeURIComponent(dataset)}?store=${encodeURIComponent(store)}`);
  return response.status === 200;
}

async function download(url) {
  if (downloadCache.has(url)) {
    return downloadCache.get(url);
  }

  const response = await fetch(url);
  if (!response.ok) {
    throw new Error(`download failed (${response.status}) for ${url}`);
  }

  const bytes = Buffer.from(await response.arrayBuffer());
  downloadCache.set(url, bytes);
  return bytes;
}

// ---- services ----------------------------------------------------------------

async function seedService(service) {
  const existing = await getMap(service.name);
  const existingIds = new Map((existing?.layers ?? []).map((layer) => [layer.dataset, layer.layerId]));

  const payload = {
    name: service.name,
    store,
    services: service.services,
    description: service.description,
    copyright: service.copyright,
    layers: service.layers.map((layer) => ({
      dataset: layer.dataset,
      name: layer.name,
      // Preserve a published id so re-running never renumbers the layers
      // (ADR-0041); a new layer asks the registry to assign the next free id.
      layerId: existingIds.get(layer.dataset) ?? -1,
      kind: layer.kind ?? "feature",
      style: persistedStyle(layer),
    })),
  };

  const response = await request(`/api/maps/${encodeURIComponent(service.name)}`, {
    method: "PUT",
    json: payload,
  });
  if (!response.ok) {
    throw new Error(await message(response));
  }

  published++;
  console.log(`  ✓ published map ${service.name} [${service.services.join(", ")}] (${service.layers.length} layer(s))`);
}

async function getMap(name) {
  const response = await request(`/api/maps/${encodeURIComponent(name)}`);
  return response.status === 200 ? response.body : null;
}

/**
 * Lowers a manifest layer's compact style and geometry to the persisted
 * MapLibre style fragment (ADR-0047): a JSON array of style-layer objects
 * carrying only type/layout/paint (the host injects id/source-layer). This
 * mirrors the workbench composer's `persistedStyle` so seeded and
 * composer-authored services draw identically.
 */
function persistedStyle(layer) {
  const style = { ...styleDefaults, ...layer.style };
  const visibility = style.visible === false ? "none" : "visible";
  const specs = [];

  if (layer.geometry === "polygon" || layer.geometry === "mixed") {
    specs.push({
      type: "fill",
      layout: { visibility },
      paint: { "fill-color": style.color, "fill-opacity": style.opacity, "fill-outline-color": style.color },
    });
  }

  if (layer.geometry === "line" || layer.geometry === "polygon" || layer.geometry === "mixed") {
    specs.push({
      type: "line",
      layout: { visibility },
      paint: { "line-color": style.color, "line-width": style.lineWidth, "line-opacity": style.opacity },
    });
  }

  if (layer.geometry === "point" || layer.geometry === "mixed") {
    specs.push({
      type: "circle",
      layout: { visibility },
      paint: {
        "circle-color": style.color,
        "circle-radius": style.radius,
        "circle-opacity": style.opacity,
        "circle-stroke-color": "#0b0f14",
        "circle-stroke-width": 1,
      },
    });
  }

  return JSON.stringify(specs);
}

async function verifyServices(selected) {
  console.log("• verifying served services");
  for (const service of selected) {
    const type = service.services.includes("map") ? "MapServer" : "FeatureServer";
    const response = await request(`/arcgis/rest/services/${encodeURIComponent(service.name)}/${type}?f=json`);
    if (!response.ok) {
      failures.push(`${service.name}: not served (${response.status})`);
      console.error(`  ✗ ${service.name}/${type} — ${response.status}`);
      continue;
    }

    const layers = response.body.layers?.length ?? 0;
    console.log(`  ✓ ${service.name}/${type} — ${layers} layer(s), capabilities "${response.body.capabilities}"`);
  }
}

// ---- plumbing ----------------------------------------------------------------

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

function contentType(format) {
  if (format === "csv") return { "Content-Type": "text/csv" };
  if (format === "ndjson" || format === "geojsonl") return { "Content-Type": "application/x-ndjson" };
  return { "Content-Type": "application/geo+json" };
}

async function message(response) {
  const body = response.body;
  if (body && typeof body === "object" && body.message) {
    return `${response.status} ${body.message}`;
  }

  return `${response.status} ${JSON.stringify(body)}`;
}

function matches(filter, value) {
  if (!filter) return true;
  return filter.has(value);
}

function listManifest() {
  console.log("Sources:");
  for (const source of sources) {
    console.log(`  ${source.id.padEnd(34)} ${source.format} EPSG:${source.srid}${source.sourceSrid ? ` (from EPSG:${source.sourceSrid})` : ""}  ${source.note ?? ""}`);
  }

  console.log("\nMaps:");
  for (const service of services) {
    console.log(`  ${service.name.padEnd(26)} ${service.services.join(",").padEnd(10)} ${service.layers.map((layer) => layer.name).join(", ")}`);
  }
}

function summarise() {
  console.log("");
  console.log(`Seed ${failures.length === 0 ? "complete" : "finished with errors"} against ${host} (${store}):`);
  console.log(`  datasets: ${ingested} loaded, ${reused} reused`);
  console.log(`  services: ${published} published`);
  if (failures.length > 0) {
    console.log(`  failures: ${failures.length}`);
    for (const failure of failures) {
      console.log(`    - ${failure}`);
    }
  }

  console.log("");
  console.log(`  GeoServices catalog: ${host}/arcgis/rest/services?f=json`);
  console.log(`  Maps:                ${host}/api/maps`);
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
