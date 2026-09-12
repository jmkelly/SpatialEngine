# Host, HTTP API, Frontend, Deployment, Security (distilled)

Implements ADR-0033 (replaces ADR-0030/0031 HTTP/workbench surfaces).

## Host API (`Spatial.Host`, ASP.NET Core minimal API, JIT)

- One JSON contract: camelCase properties and enum names; options come from
  the SDK's shared `HostApiJson`. Shapes live in `Spatial.PluginSdk.Http`;
  OpenAPI at `/openapi/v1.json` (source of the generated TypeScript wire types).
- Geometries cross as Base64 SGEOM strings, batches as Base64 SFBAT strings
  (ADR-0020). Feature data never crosses as JSON geometry.
- **Service outcomes are typed results.** `invalid.arguments` → 400,
  `not.found` → 404, `store.unavailable` → 503 (all as `ErrorResponse`
  `{code, message}`); cancelled calls → 499.
- Long work is a cancellable request, never a parked job: `CancellationToken`
  flows from the aborted connection (the demo sleep cancels over HTTP).

```
GET    /                                # identity doc (index.html when workbench served)
GET    /health/live | /health/ready     # ready includes the configured stores
POST   /api/geometry/buffer             # {geometry, distance, quadrantSegments?} -> {geometry}
POST   /api/geometry/intersection       # {left, right} -> {geometry}
POST   /api/geometry/validate           # {geometry} -> {valid}
POST   /api/geometry/simplify           # {geometry, tolerance} -> {geometry}
POST   /api/crs/describe                # {crs} -> CrsDescription
POST   /api/coordinates/transform       # {geometry, source?, target} -> {geometry}
GET    /api/catalogue?store=&pattern=   # DatasetSummary[]
GET    /api/datasets/{id}?store=        # DatasetDescription
POST   /api/datasets?store=             # {dataset, batch, srid} -> {dataset}
POST   /api/features/scan?store=        # {dataset} -> {batches[]}
POST   /api/features/query?store=       # {dataset, bbox?, filter?} -> {batches[]}
POST   /api/features/write?store=       # {dataset, batch, transaction?} -> {appended}
POST   /api/transactions/begin?store=   # -> {transaction}
POST   /api/transactions/commit?store=  # {transaction} -> {ok}
POST   /api/transactions/rollback?store=# {transaction} -> {ok}
POST   /api/demo/sleep                  # {milliseconds} -> {slept}
GET    /openapi/v1.json
GET|POST /arcgis/rest/services                                # GeoServices catalog (ADR-0035)
GET|POST /arcgis/rest/services/Geometry/GeometryServer         # Geometry Service
GET|POST /arcgis/rest/services/Geometry/GeometryServer/{op}    # project/generalize/buffer/intersect/simplify/…
GET|POST /arcgis/rest/services/{service}/FeatureServer         # FeatureServer root (layers)
GET|POST /arcgis/rest/services/{service}/FeatureServer/{layerId}
GET|POST /arcgis/rest/services/{service}/FeatureServer/{layerId}/{objectId}  # feature resource
GET|POST /arcgis/rest/services/{service}/FeatureServer/{layerId}/query
POST   /arcgis/rest/services/{service}/FeatureServer/{layerId}/addFeatures
POST   /arcgis/rest/services/{service}/FeatureServer/{layerId}/updateFeatures
POST   /arcgis/rest/services/{service}/FeatureServer/{layerId}/deleteFeatures
POST   /arcgis/rest/services/{service}/FeatureServer/{layerId}/applyEdits
```

The GeoServices routes are the Esri boundary adapter (ADR-0035): `f=json`
only, Esri JSON over HTTP, no core changes. Resources are requestable with
either GET or POST (spec §2.0.1), because ArcGIS REST JS — and therefore the
Maps SDK — POSTs reads. The facade serves Geometry
Service operations, FeatureServer queries, and — for a layer whose store
implements `IFeatureEditStore` and whose dataset has an integer identity
column — the editing operations `addFeatures`/`updateFeatures`/
`deleteFeatures`/`applyEdits` (ADR-0037). Editing is advertised per layer
via `capabilities` and field `editable`; `rollbackOnFailure` uses the
store's `ITransactionStore`. The engine API above is unchanged. Track C
consumes a remote ArcGIS REST service as a keyed
`IDataCatalogue`/`IFeatureStore` (`Spatial.Provider.ArcGisRest`).

The `store` query selects `demo` (default, always available) or `postgis`
(needs configuration).

## Configuration

| Key | Meaning |
| --- | --- |
| `Spatial:Postgis:ConnectionString` | PostGIS connection string (empty = unconfigured; every PostGIS call throws `store.unavailable`) |
| `SPATIAL_POSTGIS_CONNECTION` | Env fallback for the connection string — the **only** secret channel |
| `Spatial:WebRoot` | Built workbench directory; when set, `GET /` serves it |
| `Spatial:GeoServices:Root` | GeoServices URL prefix (default `/arcgis/rest/services`) |
| `Spatial:GeoServices:Services` | Logical Esri service `{name, store, type}` entries (`FeatureServer` only) |
| `Spatial:ArcGisRest:Services` | Remote ArcGIS REST `{name, url}` stores |
| `Spatial:ArcGisRest:Token` | Optional ArcGIS token; host config only, redacted, never in request bodies |

## Clients

- TypeScript SDK: `clients/typescript` (`@spatial/client`) — one method per
  route, wire types generated from OpenAPI (`scripts/generate.mjs`),
  drift-checked in `npm test`, includes the SFBAT decoder.
- .NET SDK: `clients/dotnet/Spatial.Client` — one typed method per route,
  core geometry values in and out, `SpatialClientException` failures.
- `eng/e2e-web.sh` proves the real host end-to-end from the TS SDK.

## Frontend boundary

- Workbench = React 19 + TypeScript + MapLibre; talks **only** to the public
  host API through the TS SDK; engine-neutral app state; no spatial logic.
  Only client-side spatial code: `src/sgeom.ts` (SGEOM → GeoJSON, byte-exact
  codec mirror) and `src/click-match.ts` (pure projection math, no pixel reads).
- Must run in a normal browser — Playwright (`eng/workbench-e2e.sh`).
- Persistence: results/recent runs in localStorage; geometry stored as the
  host-produced SGEOM base64, never re-encoded client-side.
- MapLibre worker pinned: `public/maplibre-gl-worker.mjs` via `setWorkerUrl`.

## Deployment profiles

| Profile | Shape |
| --- | --- |
| Browser/server | React assets + ASP.NET Core host + external PostGIS; services in-process. Delivered as the repository `Dockerfile` (workbench built and host published into one image on port 8080, non-root). |
| Local development | PostGIS container; hot reload; demo store for Docker-free work |

Container configuration is environment based: `SPATIAL__WEBROOT` selects the
built workbench, `SPATIAL_POSTGIS_CONNECTION` the store (absent → the store
reports `store.unavailable`), `ASPNETCORE_URLS` the bind address. See
`RELEASING.md`.

Invariants: one public API/contract set/TS SDK/React app in every profile;
host independently executable; browser tests run against the host directly.

## Security model

- Implementation projects are fully trusted in-process code.
- **Secrets flow host config → options only**
  (`PostgisOptions.ConnectionString`). Request bodies never carry
  connection material.
- **Redaction is a store diagnostic contract**: no secret in logs or
  `SpatialException` messages; unconfigured store fails with actionable
  `store.unavailable` naming the setting; failures describe config in
  redacted form (db name only) — asserted by a redaction test.
- Client-supplied text never becomes SQL structure: strict identifier
  grammar + bound parameters (see `contracts.md`).
- The demo store is read-only; writes/creation/transactions against it are
  `invalid.arguments`.
- Payload and time limits enforced where supported; diagnostics are structured.
