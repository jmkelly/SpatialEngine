# Spatial Engine

A headless, extensible spatial engine. A small, stable .NET 10 core owns the
spatial **value model** (coordinates, geometries, CRS identity, features).
Service **interfaces** in `Spatial.PluginSdk` own the **verbs** — operations
(buffer, intersection, …), data stores (PostGIS first), transformations —
implemented in-process by the `Spatial.Operations.*`,
`Spatial.Transformations.*` and `Spatial.Provider.*` projects and composed
by the independently executable host with Microsoft DI (ADR-0033).

The first delivered frontend is a browser-hosted React + MapLibre workbench.
The engine also meets the Esri ecosystem at the GeoServices REST boundary
(ADR-0035): it serves GeoServices and consumes ArcGIS REST as a provider.

**Key idea:** geometry is core, verbs are services, and interfaces outlive
implementations.

## Status

In-process services (ADR-0033). `apps/workbench-web` serves the React 19 +
TypeScript + MapLibre workbench from the host itself (`Spatial:WebRoot`):
a service/dataset catalogue, the demo dataset browser with map rendering
and coordinate-based selection, typed operation forms (buffer, scan, query,
sleep with cancellation…), result preview with browser-side persistence,
clearing of unsaved map previews, and runtime health. `eng/workbench-e2e.sh` builds the app, runs the real host
and drives the workbench with Playwright (no Docker); `demo`
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
carried onto results. It also implements the measurement, set/construction
and DE-9IM relation verbs (ADR-0036). Adapters never expose
NetTopologySuite types (ADR-0005).

The Esri GeoServices REST boundary (ADR-0035) is served by
`Spatial.Adapter.GeoServices` at `Spatial:GeoServices:Root`
(`/arcgis/rest/services` by default): a catalog, a Geometry Service
(`project`, `generalize`, `buffer`, `intersect`, `simplify`-as-repair,
`union`, `difference`, `convexHull`, `densify`, `relation`, measures) and a
FeatureServer over the keyed stores (query, the feature resource, and gated
editing), all `f=json` on GET or POST. The shared
`Spatial.Interop.Esri` project owns the Esri wire codec, the curated
WKID ↔ EPSG map, the Esri error model, the closed `where` filter grammar
and the per-feature edit results. `Spatial.Provider.ArcGisRest` consumes a
configured remote ArcGIS REST service through
`IDataCatalogue`/`IFeatureStore` with pagination and `where` pushdown.
Feature editing (`addFeatures`/`updateFeatures`/`deleteFeatures`/
`applyEdits`, ADR-0037) is served for layers whose store implements the
additive `IFeatureEditStore` capability and whose dataset has an integer
identity column; the demo and ArcGIS REST stores stay read-only.

## Repository layout

| Path | Purpose |
| --- | --- |
| `src/Spatial.Core` | Spatial value model (no dependencies, no algorithms) |
| `src/Spatial.PluginSdk` | Service interfaces, DTOs, error codes, HTTP shapes |
| `src/Spatial.Operations.NetTopologySuite` | Geometry operations (buffer, intersection, validate, simplify) plus measures/processing/relations (ADR-0036) |
| `src/Spatial.Transformations.ProjNet` | CRS description and coordinate transformation |
| `src/Spatial.Interop.Esri` | Shared Esri JSON codec, WKID map, error model and filter grammar (ADR-0035) |
| `src/Spatial.Adapter.GeoServices` | GeoServices REST serving facade (catalog, Geometry Service, FeatureServer query + editing) |
| `src/Spatial.Provider.ArcGisRest` | ArcGIS REST consuming provider (ADR-0035) |
| `src/Spatial.Provider.PostGIS` | Data store: catalogue, dataset, feature scan/query/write, transactions and editing (ADR-0037) |
| `src/Spatial.Provider.Demo` | Docker-free demo store + cancellable sleep |
| `src/Spatial.Host` | Independently executable ASP.NET Core host (typed routes, DI composition) |
| `src/Spatial.AppHost` | Aspire AppHost for the local development profile (ADR-0034) |
| `tests/` | unit / architecture / integration suites |
| `architecture/` | Plan, principles, ADR register, boundary docs, ADRs and reference specs (incl. GeoServices) |
| `clients/ apps/` | SDKs and web apps |
| `eng/` | Build, format, test, verify scripts |

## Requirements

- .NET SDK 10.0.400 (pinned in `global.json`)
- Node 26 (`.node-version`) — needed for the workbench

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

1. `architecture/decisions/` — architecture decision records (the source of truth)
2. `architecture/principles.md` — the twenty principles
3. `architecture/geoservices-implementation-plan.md` — Esri GeoServices REST track (ADR-0035, ADR-0037)
4. `AGENTS.md` — boundaries and guidance for development agents

## Delivered milestone

- **Milestone 1** — browser workbench: PostGIS → core geometry → geometry
  service → rendered, inspectable, persistable result.
