# Spatial Engine

**Spatial infrastructure you can build on — not another GIS monolith.**

Spatial Engine gives you a small, dependable server that owns spatial
*values* and performs spatial *work*: buffer, intersect, validate, simplify,
measure, transform between CRSs, and read/write real feature data. It ships
with a browser workbench so you can see results on a map immediately, two
typed SDKs so you can script it in minutes, and an Esri GeoServices REST
boundary so the tools you already use can talk to it.

No desktop install. No plugin zoo. No vendor lock-in on your geometry.

- **Start without a database.** A Docker-free demo store makes the engine
  usable the moment it starts.
- **Grow into your data.** Point it at PostGIS, or consume a remote ArcGIS
  REST service as if it were local.
- **Build clients your way.** One typed HTTP API, an OpenAPI document, a
  TypeScript SDK and a .NET SDK.
- **Extend without forking.** Implement a service interface, compose it with
  DI, never touch the core.

---

## Why Spatial Engine

**Geometry is treated as a value, not a row in someone's SDK.** The
`Spatial.Core` type model — coordinates, geometries, CRS identity, features —
is dependency-free and immutable. Every service boundary speaks in those
types only.

**Spatial algorithms are services, not the core.** Buffering, intersection,
validity, simplification, measurement, set/construction and DE-9IM relations
live behind small SDK interfaces and are implemented on battle-tested
libraries (NetTopologySuite, ProjNet). Swap or add an implementation without
disturbing the value model.

**Your existing GIS keeps working.** The engine serves the Esri GeoServices
REST shape — catalog, Geometry Service, FeatureServer, gated editing — and
consumes remote ArcGIS REST as a provider. The compatibility claim is proven
against the official ArcGIS REST JS client.

**Your data and secrets stay under your control.** PostGIS is a first-class
store with catalogue, scan/query/write, transactions and gated editing.
Connection strings flow from host configuration to options only — never
through request bodies, logs or error messages.

**Long work is cancellable, not a parked job.** Every call is a cancellable
`Task`; the client disconnecting cancels the server work. Failures are
structured codes (`invalid.arguments`, `not.found`, `store.unavailable`)
that map to real HTTP statuses.

**It is genuinely inspectable.** `eng/verify.sh` gates format, build and the
full test suite; quality gates enforce zero warnings, branch coverage,
complexity and CRAP thresholds; and two end-to-end suites drive a real host
from the JavaScript SDK and from a real browser.

---

## See it in five minutes

You need .NET SDK 10.0.400 (pinned in `global.json`). Node 26 is only needed
for the browser workbench.

**1. Run the engine.** The demo store is always available, so nothing else
is required:

```bash
dotnet run --project src/Spatial.Host
# → http://localhost:5201
curl -s http://localhost:5201/health/ready
curl -s http://localhost:5201/api/catalogue
```

**2. Open the workbench.** Build the React app once and let the host serve it
from the same origin:

```bash
cd apps/workbench-web && npm ci && npm run build && cd ../..
Spatial__WebRoot="$(pwd)/apps/workbench-web/dist" \
  dotnet run --project src/Spatial.Host
# → open http://localhost:5201
```

**3. Bring a real store.** With Docker available, the Aspire AppHost starts
PostGIS, the Seq log server and the Vite dev server:

```bash
dotnet run --project src/Spatial.AppHost
```

The Aspire dashboard prints the workbench, host and Seq endpoints; host logs
stream to the Seq UI (ADR-0045). Prefer to skip Docker? Serve the built
workbench from the host as above.

### Verify the whole thing

```bash
./eng/verify.sh          # format check + build + full test run
./eng/e2e-web.sh         # real host, driven by the TypeScript SDK over HTTP
./eng/workbench-e2e.sh   # real host + built workbench + Playwright
./eng/cli-e2e.sh         # real host driven by the Spatial CLI
```

### Seed it with real data

`eng/seed.sh` fetches real, publicly available data (Natural Earth, USGS),
loads it through the ingest API — including a server-side reprojection — and
publishes a set of styled feature and map services. Start a host with an
admin token and run:

```bash
SPATIAL_ADMIN_TOKEN=seed-admin-token ./eng/seed.sh
```

See [`tools/seed/README.md`](tools/seed/README.md) for the datasets and
services, and `--only`/`--force`/`--list` options.

### Drive it from the CLI

`Spatial.Cli` is a self-contained, dependency-free command-line client of the
public host API (ADR-0052) built for scripts and LLMs: add datasets, compose
styled maps out of layers, keep the workspace in a declarative
`spatial.json`, and export the FeatureServer/MapServer/ImageServer endpoints a
service projects to.

```bash
# list what a running host advertises
dotnet run --project clients/dotnet/Spatial.Cli -- dataset list

# add a dataset from a file, then publish a styled map service from it
dotnet run --project clients/dotnet/Spatial.Cli -- \
  dataset add --file places.geojson --dataset public.places --srid 4326
dotnet run --project clients/dotnet/Spatial.Cli -- \
  map create --name WorldPlaces --kind map --layer public.places=Places
dotnet run --project clients/dotnet/Spatial.Cli -- \
  map set-style --map WorldPlaces --dataset public.places --geometry point --color '#ffd54f'
dotnet run --project clients/dotnet/Spatial.Cli -- \
  map export WorldPlaces --format url

# or replay a whole workspace declaratively
SPATIAL_ADMIN_TOKEN=my-token dotnet run --project clients/dotnet/Spatial.Cli -- project apply
```

Use `--json` for a stable `{ok, command, data}` envelope and `--help` for the
descriptive flag reference. See
[`architecture/distilled/cli.md`](architecture/distilled/cli.md) for the
project-file schema and the full command surface.

CI runs the four verification scripts plus the JavaScript typecheck,
generated-types drift check and unit suites on every push and pull request.

---

## What you get out of the box

**A browser workbench** (`apps/workbench-web`, React 19 + TypeScript +
MapLibre) that a first-time user can operate without documentation:

- browse the service and dataset catalogue, with a live runtime health view;
- render features from the demo or PostGIS store on a map and select them by
  coordinate;
- run typed operation forms — buffer, scan, query, cancellable sleep — and
  preview results without leaving the page;
- compose a map or feature service: stack datasets into ordered layers,
  style them on the map, reorder by drag and drop, import a GeoJSON/NDJSON/
  CSV file inline, then publish and reopen the service;
- keep unsaved previews in the browser, and clear them when you are done.

**A headless engine** with a typed HTTP surface: geometry operations, CRS
description and coordinate transformation, catalogue and dataset endpoints,
feature scan/query/write, transactions, health checks and an OpenAPI document
at `/openapi/v1.json`. Geometries cross as canonical Base64 SGEOM;
feature batches as Base64 SFBAT — not lossy JSON.

**Two SDKs that stay honest:** `clients/typescript` (`@spatial/client`, wire
types generated from the OpenAPI snapshot and drift-checked in `npm test`)
and `clients/dotnet/Spatial.Client` (one typed method per route, core
geometry values in and out).

**An Esri boundary you can point at:** GeoServices REST at
`/arcgis/rest/services` by default — a catalog, a Geometry Service
(`project`, `generalize`, `buffer`, `intersect`, `simplify`, `union`,
`difference`, `convexHull`, `densify`, `relation`, measures) and a
FeatureServer over your keyed stores, with editing gated per layer. In the
other direction, `Spatial.Provider.ArcGisRest` reads a configured remote
ArcGIS REST service through the same store interfaces, with pagination and
`where` pushdown.

---

## Ship it

The engine packages as one container: the host plus the built workbench,
listening on port 8080 as a non-root user.

```bash
docker build -t spatial-engine:0.1.0 .
docker run --rm -p 8080:8080 \
  -e SPATIAL_POSTGIS_CONNECTION="Host=…;Database=…;Username=…;Password=…" \
  spatial-engine:0.1.0
```

The demo store and the GeoServices FeatureServer are always available; the
PostGIS store is advertised once its connection string is configured. See
`RELEASING.md` for the version/tag checklist and `CHANGELOG.md` for what
shipped.

---

## Under the hood

### Repository layout

| Path | Purpose |
| --- | --- |
| `src/Spatial.Core` | Spatial value model (no dependencies, no algorithms) |
| `src/Spatial.PluginSdk` | Service interfaces, DTOs, error codes, HTTP shapes |
| `src/Spatial.Operations.NetTopologySuite` | Geometry verbs: operations, measures, processing, relations |
| `src/Spatial.Transformations.ProjNet` | CRS description and coordinate transformation |
| `src/Spatial.Interop.Esri` | Shared Esri JSON codec, WKID map, error model, filter grammar |
| `src/Spatial.Adapter.GeoServices` | GeoServices REST serving facade |
| `src/Spatial.Provider.ArcGisRest` | ArcGIS REST consuming provider |
| `src/Spatial.Provider.PostGIS` | PostGIS store: catalogue, features, transactions, editing |
| `src/Spatial.Provider.Demo` | Docker-free demo store and cancellable sleep |
| `src/Spatial.Host` | Independently executable ASP.NET Core host (DI composition) |
| `src/Spatial.AppHost` | Aspire AppHost for the local development profile |
| `clients/`, `apps/` | SDKs and the browser workbench |
| `tests/` | Unit, architecture and integration suites |
| `architecture/` | Principles, ADRs, boundary docs and reference specs |
| `eng/` | Build, format, test and verify scripts |

### The shape of the design

A small stable core owns spatial values. Interfaces in `Spatial.PluginSdk`
own the verbs. Implementations live in their own projects, depend on
contracts and never on each other, and are composed by the host with
Microsoft DI. Third-party types — NTS, Npgsql, EF, renderer — never cross a
public boundary. The architecture tests enforce these walls, and every rule
names the principle or ADR it implements.

### Reading order for contributors

1. `architecture/decisions/` — the ADR register, the source of truth.
2. `architecture/principles.md` — the twenty principles.
3. `architecture/distilled/` — condensed docs routed by task.
4. `AGENTS.md` — boundaries and guidance for development agents.

---

## Requirements

- .NET SDK 10.0.400 (pinned in `global.json`)
- Node 26 (`.node-version`) for the workbench
- Docker, optionally, for PostGIS and the Aspire development profile

## License

MIT — see `LICENSE`.
