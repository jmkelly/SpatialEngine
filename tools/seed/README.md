# Seed: realistic on-demand spatial data

`eng/seed.sh` fetches real, publicly available spatial data, loads it through
the neutral ingest API and publishes a set of styled maps exposing feature and
map services. It exists so a fresh host (or a demo/test environment) can be
given a non-trivial dataset in one command.

```bash
# Start (or reuse) a host, seed it, and leave it running:
./eng/seed.sh

# Seed a host you already run:
SPATIAL_SEED_HOST=http://127.0.0.1:5201 \
SPATIAL_ADMIN_TOKEN=my-token \
  ./eng/seed.sh

# Just one service, forcing a reload of its datasets:
./eng/seed.sh --only=WorldReference --force

# See what would be seeded, without touching the host:
node tools/seed/seed.mjs --list
node tools/seed/seed.mjs --dry-run
```

## How it reaches the host

`seed.mjs` first POSTs the manifest to `POST /api/seed` (ADR-0070): on a
Development host the seed runs server-side — download, decode, reproject,
atomic ingest, map publication — so a token-less host (such as the Aspire
DevHost's) seeds with no restart. The endpoint requires the admin token only
when one is configured and does not exist outside Development. Hosts that
answer 404/405 get the legacy drive instead: `seed.mjs` downloads each
source itself and walks `POST /api/ingest` + `PUT /api/maps/{name}` with the
configured token.

## What it seeds

Everything is data in [`manifest.mjs`](./manifest.mjs) — add an entry to add a
source or a service.

**Datasets** (from the internet, loaded via `POST /api/ingest`):

| Dataset | Source | Note |
| --- | --- | --- |
| `public.world_countries` | Natural Earth 1:110m | editable (auto identity) |
| `public.world_places` | Natural Earth 1:110m | points |
| `public.world_rivers` | Natural Earth 1:110m | lines |
| `public.world_lakes` | Natural Earth 1:110m | polygons |
| `public.us_states` | Natural Earth 1:50m | ~1.4k admin-1 polygons |
| `public.earthquakes` | USGS feed | magnitude 2.5+, past 7 days |
| `public.world_places_mercator` | Natural Earth 1:110m | **reprojected 4326 → 3857 on ingest** |

**Maps** (`PUT /api/maps/{name}`, ADR-0053):

- Feature maps (`services: ["feature"]`): `WorldCountries`, `WorldPlaces`,
  `UnitedStates`, `SeismicActivity`, `WorldPlacesMercator`.
- Map services (MapServer, ADR-0048): `WorldReference`, `WorldAtlas`,
  `SeismicMap` — each layer's persisted style (ADR-0047) is lowered to
  `drawingInfo`.
- `Census` (MapServer + FeatureServer): the `public.us_states` states as a
  Census-2000-shaped `States` layer, so the workbench Parity page's Map tab
  compares like for like (Esri `Census` vs localhost `Census` over the same
  US bbox) instead of a world reference map against US census data. The
  Aspire DevHost re-seeds just this map on every boot (`parity-seed` step),
  because restarts wipe the `memory` store; anything beyond it is an
  explicit `./eng/seed.sh`.

## Conformance host and the qgis map

`eng/conformance.sh` (slice A, T-066) is the one-command entry point for
visual parity work: it builds the host, starts it on port 5251 when none is
running (`SPATIAL_CONFORMANCE_PORT` overrides), loads everything above, and
then wires the `qgis` WMS map (demo `cities` points plus memory `routes`
lines and `zones` polygons) via [`qgis-map.mjs`](./qgis-map.mjs). The map
definition is read from `tests/fixtures/qgis/qgis-4.2.2-wms.json` — the same
fixture `QgisReplayTests` replays — so the replay suite, this seed, and the
later conformance slices share one map.

```bash
# One command: host + seed data + qgis map, left running for visual work:
./eng/conformance.sh
```

## How it works

- `seed.mjs` is a **pure client of the public API**; the only spatial logic is
  in the manifest, and the only style logic lowers the manifest's compact
  draw recipe to the persisted MapLibre fragment (ADR-0047). Conversions (e.g.
  the EPSG:4326 → EPSG:3857 reprojection) run **inside the engine** via the
  ingest `sourceSrid` parameter and the ProjNet transformation service.
- Re-running is safe: an existing dataset is reused (unless `--force`) and an
  existing map keeps its stable layer ids (ADR-0041).
- It does not run in `eng/verify.sh`: it depends on the network.
