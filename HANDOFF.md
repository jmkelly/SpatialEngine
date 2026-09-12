# Handoff — Spatial Engine

> Written for the next agent taking over. Read `AGENTS.md`, then
> `architecture/decisions/ADR-0033-in-process-interfaces.md`. **The worker
> plugin model is retired** — services are in-process interfaces composed by
> DI; ADRs 0002/0003/0006–0008/0013/0022–0028/0030/0031 are superseded
> history. The GeoServices REST track is the active work.

## Repository state

- **Branch:** main. The quality-gate queue files (if any) are gitignored —
  do not commit them.
- **Model:** `Spatial.Core` (values, zero deps) → `Spatial.PluginSdk`
  (interfaces: `IGeometryOperations`, `ICrsDirectory`,
  `ICoordinateTransforms`, `IDataCatalogue`, `IFeatureStore`,
  `ITransactionStore`, `IDemoJobs`, `SpatialException`, DTOs, HTTP shapes)
  → implementations (`NtsGeometryOperations`, `ProjNetTransforms`,
  `DemoStore`, `PostgisStore`) → `Spatial.Host` (typed routes, keyed DI
  `"demo"`/`"postgis"`, `PostgisOptions`).
- **Tests:** `eng/verify.sh` is the gate. Counts (post-refactor):
  Core 308, NTS 28, ProjNet 61, Demo 15, PostGIS 130 unit, Client 9,
  Host 14, Architecture 8, PostGIS integration 16 (skip without Docker).
  TS SDK 7+1 skip; workbench unit 22; Playwright 5.
- **Web:** `eng/e2e-web.sh` (real host + TS SDK) and
  `eng/workbench-e2e.sh` (real host + built app + Playwright chromium, 5
  specs) are green.

## Hard-won gotchas (read before touching this code)

- **STJ cannot bind the core structs.** `FeatureSchema` (ctor param
  `fields` vs property `Fields`) and `FieldDefinition` (readonly struct)
  silently deserialize to defaults. The explicit
  `FeatureSchemaConverter`/`FieldDefinitionConverter` in
  `Spatial.PluginSdk.Http` are load-bearing — do not remove them.
- **NaN never survives JSON.** `JSON.stringify(NaN)` is `null` (TS side);
  the .NET `HostApiJson` allows named floating-point literals, so the .NET
  client can send NaN but browsers cannot. Invalid-number tests should use
  `quadrantSegments: 0` or bad Base64 instead.
- **`DatasetDescription.Schema` must stay concrete `FeatureSchema`.**
  STJ cannot deserialize `IFeatureSchema`; the interface-typed version
  broke the catalogue round trip.
- **MapLibre must not be queried via readPixels in headless CI.**
  Selection stays coordinate-based (`click-match.ts`); the suite fires
  `map.fire("click", …)` at lng/lat `(0,0)`.
- **MapLibre's worker file must be pinned**: `public/maplibre-gl-worker.mjs`
  + `setWorkerUrl("/…")` in `MapScreen.tsx`.
- **`Spatial:WebRoot` static middleware must run BEFORE `app.UseRouting()`**.
- **`dotnet run` orphans its apphost**: `eng/*e2e.sh` kill both the wrapper
  and `Spatial.Host --urls` and refuse a busy port.
- **React effects capture stale state**: the map's mount-once effect calls
  the CURRENT `selectFeature` via `actionsRef`.
- **`npm ci` in the workbench re-creates the SDK `file:` dep** — regenerate
  with `npm install` when the SDK changes and the lock drifts.
- **Style/quality traps still apply** (CA1859/CA1826/CA1068/CA1305/CA1861,
  `Results<…>` typed results, format before commit).

## GeoServices REST — status

The GeoServices track (ADR-0035/0036/0037/0038,
`architecture/geoservices-implementation-plan.md`) is implemented and its
compatibility claim is now proven against a real Esri client. Completed:

1. **Real-client proof.** `clients/typescript/test/geoservices-e2e.test.ts`
   drives the live host with the official **ArcGIS REST JS** libraries — the
   request layer ArcGIS Maps SDK for JS uses — and runs inside
   `eng/e2e-web.sh`. It found and closed three real gaps: POST resource reads
   (`getService`/`getLayer`), the Esri match-all `where=1=1`, and the Feature
   (object) resource `FeatureServer/<layerId>/<objectId>` that `getFeature`
   reads.
2. **Recorded-corpus gaps** (`research/arcgis/README.md`): group layers are
   skipped by the provider (`A_group_layer_is_not_listed_as_a_dataset`) and
   tables stay as non-spatial datasets; `esriGeometryEnvelope` and
   `esriFieldTypeGUID` are codec-supported and pinned by unit tests (the
   corpus simply has no such real layer); SRIDs outside `WkidMap` remain a
   deliberate allow-list.
3. **Five Geometry Service operations** (`offset`, `cut`, `reshape`,
   `trimExtend`, `autoComplete`) are recorded non-goals in the plan and
   compatibility review, rejected with a typed `invalid.arguments` failure
   and pinned by `GeometryServiceTests`.
4. The `research/arcgis` corpus is a provider regression gate: the recorded
   fixtures are copied into `Spatial.Provider.ArcGisRest.Tests` and run by
   `eng/verify.sh`.

`eng/verify.sh` is green, and so are all four quality-loop gates
(warnings, coverage, CRAP, metrics) after the 2026-09-12 facade/CRAP
cleanup: `FeatureService` split into query/edit/geometry engines,
`SpatialClient` into client + transport, `PostgisStore`'s stateless leaves
into `PostgisWriteOperations`/`PostgisPredicate`, and `EsriFilterClause`'s
comparison primitives into `EsriFilterLogic`. The quality-loop queues are
gitignored; re-run the audits before trusting the AGENTS baseline numbers.
