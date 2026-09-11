# Spatial Engine

A headless, extensible spatial engine. A small, stable .NET 10 core owns the
spatial **value model** (coordinates, geometries, CRS identity, features).
Service **interfaces** in `Spatial.PluginSdk` own the **verbs** — operations
(buffer, intersection, …), data stores (PostGIS first), transformations —
implemented in-process by the `Spatial.Operations.*`,
`Spatial.Transformations.*` and `Spatial.Provider.*` projects and composed
by the independently executable host with Microsoft DI (ADR-0033).

The first delivered frontend is a browser-hosted React + MapLibre workbench.
A thin Tauri 2 shell packages the unchanged web client afterwards; it
contains no spatial logic.

**Key idea:** geometry is core, verbs are services, and interfaces outlive
implementations.

## Status

In-process services (ADR-0033). `apps/workbench-web` serves the React 19 +
TypeScript + MapLibre workbench from the host itself (`Spatial:WebRoot`):
a service/dataset catalogue, the demo dataset browser with map rendering
and coordinate-based selection, typed operation forms (buffer, scan, query,
sleep with cancellation…), result preview with browser-side persistence,
clearing of unsaved map previews, and runtime health. `eng/workbench-e2e.sh` builds the app, runs the real host
and drives the workbench with Playwright (no Tauri, no Docker); `demo`
provides the Docker-free datasets and the cancellable sleep; the geometry
adapter (`src/sgeom.ts`) decodes canonical SGEOM bytes straight from the
host to the map.

The independently executable host serves the typed HTTP API
(`architecture/distilled/host-and-clients.md`): `POST /api/geometry/*`,
`POST /api/crs/describe`, `POST /api/coordinates/transform`,
`GET /api/catalogue`, `GET /api/datasets/{id}`, `POST /api/datasets`,
`POST /api/features/scan|query|write`, `POST /api/transactions/*`,
`POST /api/demo/sleep`, health, and the OpenAPI description at
`/openapi/v1.json`. Geometries cross as Base64 SGEOM, batches as Base64
SFBAT; failures are structured `SpatialException` codes
(`invalid.arguments` → 400, `not.found` → 404, `store.unavailable` → 503).
Two SDKs ship: `clients/typescript` (`@spatial/client`: fetch-based, wire
types generated from the OpenAPI snapshot, the canonical SFBAT
feature-batch decoder, drift checked in `npm test`) and
`clients/dotnet/Spatial.Client` (.NET typed client with unit and real-host
integration tests). `eng/e2e-web.sh` runs the real host process and drives
it from the TypeScript SDK over real HTTP.

The PostGIS store (`Spatial.Provider.PostGIS`) implements catalogue,
dataset, feature scan/query/write and transactions on Npgsql 10: schema
discovery, streaming reads collected to canonical batches, bounding-box
and parameterised attribute filtering, single-transaction appends,
result-table creation and commit/rollback over store-owned handles.
Secrets stay host-managed (`Spatial:Postgis:ConnectionString` /
`SPATIAL_POSTGIS_CONNECTION`, never in request bodies). Covered by unit
tests plus a containerised integration suite (Testcontainers PostGIS) that
also proves cancellation and secret redaction.

The CRS service (`Spatial.Transformations.ProjNet`) implements description
and transformation on ProjNet 2.1 with a curated embedded EPSG catalogue.
The engine's x-first coordinate convention is ProjNet's own math-transform
order, so no axis swaps are needed. Results keep Z/M and layout and are
stamped with the target CRS.

The geometry service (`Spatial.Operations.NetTopologySuite`) implements
buffer, intersection, OGC validity (an invalid geometry is a successful
`false`) and Douglas-Peucker simplification, with the input CRS identity
carried onto results. Adapters never expose NetTopologySuite types
(ADR-0005).

## Repository layout

| Path | Purpose |
| --- | --- |
| `src/Spatial.Core` | Spatial value model (no dependencies, no algorithms) |
| `src/Spatial.PluginSdk` | Service interfaces, DTOs, error codes, HTTP shapes |
| `src/Spatial.Operations.NetTopologySuite` | Geometry operations (buffer, intersection, validate, simplify) |
| `src/Spatial.Transformations.ProjNet` | CRS description and coordinate transformation |
| `src/Spatial.Provider.PostGIS` | Data store: catalogue, dataset, feature scan/query/write and transactions |
| `src/Spatial.Provider.Demo` | Docker-free demo store + cancellable sleep |
| `src/Spatial.Host` | Independently executable ASP.NET Core host (typed routes, DI composition) |
| `src/Spatial.AppHost` | Aspire AppHost for the local development profile (ADR-0034) |
| `tests/` | unit / architecture / integration suites |
| `architecture/` | Plan, principles, ADR register, boundary docs, ADRs and reference specs (incl. GeoServices) |
| `contracts/ clients/ apps/` | SDKs and web/desktop apps |
| `eng/` | Build, format, test, verify scripts |

## Requirements

- .NET SDK 10.0.400 (pinned in `global.json`)
- Node 26 (`.node-version`) — needed from Milestone 1 onward

## Quickstart

```bash
./eng/verify.sh    # format check + build + full test run
```

### Local development (Aspire)

`src/Spatial.AppHost` (ADR-0034) composes the whole local profile — a
PostGIS container, `Spatial.Host` with its connection string injected, and
the Vite workbench dev server:

```bash
dotnet run --project src/Spatial.AppHost
```

Requires Docker and Node ≥ 22.6. The Aspire dashboard prints the workbench
and host endpoints. Without Docker, serve the built workbench from the
host directly:

```bash
dotnet run --project src/Spatial.Host
```

## Reading order for new contributors

1. `architecture/implementation-plan.md` — the full plan (source of truth)
2. `architecture/principles.md` — the twenty principles
3. `architecture/decisions/` — architecture decision records (ADR-0035 is current)
4. `architecture/geoservices-implementation-plan.md` — Esri GeoServices REST track (ADR-0035)
5. `AGENTS.md` — boundaries and guidance for development agents

## Planned milestones

- **Milestone 1** — browser workbench: PostGIS → core geometry → geometry
  service → rendered, inspectable, persistable result.
- **Milestone 2** — Tauri 2 desktop packaging of the unchanged web client.
