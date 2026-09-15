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
GET    /routes                          # HTML index of every route and published service
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
GET    /api/maps                        # Map[]
GET    /api/maps/{name}                 # Map
PUT    /api/maps/{name}                 # create/replace -> Map (admin)
DELETE /api/maps/{name}                 # -> {deleted} (admin)
POST   /api/maps/{name}/render          # persisted layer styles -> image
GET    /api/maps/{name}/tiles/{z}/{x}/{y}.{fmt}  # map tile (Tiles service)
GET    /arcgis/rest/services/{name}/ImageServer  # raster layers (Image service)
GET|POST /ogc/{name}/wms                 # OGC WMS 1.3.0 + 1.1.1 caps dialect (Wms service): GetCapabilities negotiates the dialect (absent/1.3.x -> 1.3.0, 1.1.x -> 1.1.1 with SRS + LatLonBoundingBox, else InvalidParameterValue); GetMap PNG/JPEG (EXCEPTIONS=XML/INIMAGE/BLANK + se_xml/se_inimage/se_blank; DPI triple scales symbology at 96-dpi reference, frame unchanged; JPEG+TRANSPARENT stays lenient; absent BGCOLOR paints opaque white; SLD=/SLD_BODY= is OperationNotSupported), GetLegendGraphic per-layer style PNG (+LegendURL per style), GetFeatureInfo text/plain+text/html+text/xml+JSON+GML with FEATURE_COUNT cap; EPSG:4326 is lat-first for 1.3.x but x-first for 1.1.1, and GetMap requires VERSION; every layer advertises one default Style (STYLES= or STYLES=default renders it, any other name is StyleNotDefined). Deliberate non-goals (T-014, confirmed T-045 diagnostics): GetStyles + DescribeLayer (no recorded trace sends them), TIME/WMS-T (no temporal caps advertised), vendor params (1.1.1 GetMap/GetFeatureInfo KVP already works).
GET|POST /ogc/{name}/wfs                 # OGC WFS 2.0.0 (Wfs service): GetCapabilities (served outputFormats advertised, GML absent), DescribeFeatureType XSD, GetFeature GeoJSON with startIndex/count paging over a stable order (sortBy else feature-id order; numberMatched/numberReturned/next envelope), srsName response reprojection, bbox subset; FES filter/CQL/resourceId and GetPropertyValue/stored-query ops reject by name; WFS-T stays a non-goal (gated Esri edits + neutral ingest are the write path)
POST   /api/ingest?store=&dataset=&srid=&format=&identity=&identityField=&publish=&sourceSrid=
                                       # raw/multipart upload -> IngestResult;
                                       # sourceSrid reprojects via ICoordinateTransforms
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
GET|POST /arcgis/rest/services/{service}/MapServer                   # MapServer root (spec §4, ADR-0048)
GET|POST /arcgis/rest/services/{service}/MapServer/layers            # all layers and tables
GET|POST /arcgis/rest/services/{service}/MapServer/{layerId}         # layer metadata (+ drawingInfo)
GET|POST /arcgis/rest/services/{service}/MapServer/{layerId}/query   # layer query (FeatureServer engine)
GET|POST /arcgis/rest/services/{service}/MapServer/identify
GET|POST /arcgis/rest/services/{service}/MapServer/find
GET|POST /arcgis/rest/services/{service}/MapServer/export            # f=image bytes or {href}
GET|POST /arcgis/rest/services/{service}/MapServer/tile/{z}/{y}/{x}  # Web-Mercator tile
GET|POST /arcgis/rest/services/{service}/MapServer/exportTiles      # reject by name (ADR-0060)
GET|POST /arcgis/rest/services/{service}/MapServer/estimateExportTileSize # reject by name (ADR-0060)
GET|POST /arcgis/rest/services/{service}/MapServer/WMTS[{/*rest}]    # reject by name (ADR-0060)
GET|POST /arcgis/rest/services/{service}/MapServer/generateKml      # reject by name (ADR-0060)
GET|POST /arcgis/rest/services/{service}/MapServer/kml/{*rest}      # reject by name (ADR-0060)
GET|POST /arcgis/rest/services/{service}/MapServer/jobs[{/*rest}]   # reject by name (ADR-0060)
GET|POST /arcgis/rest/services/{service}/ImageServer                 # ImageServer root (spec §8, ADR-0051)
GET|POST /arcgis/rest/services/{service}/ImageServer/exportImage    # f=image bytes or {href}
GET|POST /arcgis/rest/services/{service}/ImageServer/identify       # pixel values + catalog items
GET|POST /arcgis/rest/services/{service}/ImageServer/query          # raster catalog query (safe where subset)
GET|POST /arcgis/rest/services/{service}/ImageServer/download       # raw file ids (opt-in, size-capped)
GET|POST /arcgis/rest/services/{service}/ImageServer/file           # raw file bytes (range-capable)
GET|POST /arcgis/rest/services/{service}/ImageServer/{rasterId}     # raster catalog item
GET|POST /arcgis/rest/services/{service}/ImageServer/{rasterId}/info # raster info
GET|POST /arcgis/rest/services/{service}/ImageServer/{rasterId}/image # one item's exported image (spec §8.2)
GET|POST /arcgis/rest/services/{service}/ImageServer/{rasterId}/thumbnail # one item's thumbnail (spec §8.3)
GET|POST /arcgis/rest/services/{service}/ImageServer/legend      # per-band swatches (ADR-0054)
GET|POST /arcgis/rest/services/{service}/ImageServer/find        # catalog text search (ADR-0054)
GET|POST /arcgis/rest/services/{service}/ImageServer/statistics  # stored band stats (ADR-0054)
GET|POST /arcgis/rest/services/{service}/ImageServer/computeHistograms # per-band histograms (ADR-0054)
GET|POST /arcgis/rest/services/{service}/ImageServer/rasterAttributeTable # class table (ADR-0054)
GET|POST /arcgis/rest/services/{service}/ImageServer/thumbnail   # dataset thumbnail (ADR-0054)
GET|POST /arcgis/rest/services/{service}/ImageServer/metadata    # authored service metadata XML (ADR-0068; 404 without authoring)
GET|POST /arcgis/rest/services/{service}/ImageServer/{rasterId}/metadata # authored per-item metadata XML (ADR-0068; 404 without authoring)
GET|POST /arcgis/rest/services/{service}/ImageServer/measure…      # mensuration ops rejected by name (ADR-0059)
GET|POST /arcgis/rest/services/{service}/ImageServer/multidimensionalInfo|slices # rejected by name (ADR-0059)
GET|POST /arcgis/rest/services/{service}/ImageServer/addRasters…   # catalog writes rejected by name (ADR-0059)
GET|POST /arcgis/admin/services                                # admin projection (ADR-0041), token-gated
GET|POST /arcgis/admin/services/{name}.{type}
POST   /arcgis/admin/services/{name}.{type}/createService
POST   /arcgis/admin/services/{name}.{type}/deleteService
POST   /arcgis/admin/uploads
POST   /arcgis/admin/uploads/{id}/publish
```

Maps are the neutral authoring and exposure model (ADR-0053): a named,
ordered set of styled layers from one keyed store plus the set of services it
exposes (FeatureServer, MapServer, Tiles, Wms, Wfs, ImageServer), with persisted stable layer
ids. `GET /api/maps` and
`GET /api/maps/{name}` are always available; `PUT`/`DELETE` and
`POST /api/ingest` are mounted only when `Spatial:Admin:Token`
(`SPATIAL_ADMIN_TOKEN`) is configured, and then require it
(`Authorization: Bearer …` or `?token=`). The Esri admin projection at
`Spatial:GeoServices:AdminRoot` (default `/arcgis/admin`) is the same
surface behind a token-gated Esri error envelope; without a configured
token it returns an actionable unavailable error.

A map exposes a service only when its `Services` set contains it; the
GeoServices catalog advertises `FeatureServer`/`MapServer`/`ImageServer` per
enabled service, and the OGC tile/WMS/WFS routes resolve the same map. The
pre-ADR-0053 `/api/publications` aliases were removed in 0.2.0;
`/api/maps` is canonical (unknown routes answer 404). Deliberate Esri non-goals (scope in `research/compat/scope.md`,
full audit in `architecture/references/geoservices-compatibility.md` §7.1):
Geocode Server, GP Server (no engine job model — ADR-0033), Network
Analysis, GeoEvent/Stream/Knowledge/Workflow/Data Store admin, token/auth
(498/499), and the `f=html` Services Directory (a typed
`invalid.arguments` naming the JSON surface). The catalog only advertises
served types (`GeometryServer`/`FeatureServer`/`MapServer`/`ImageServer`);
`GPServer`-style entries from real catalogs are omitted, never emulated
(T-049 honesty tests pin this with the sampleserver6 replay).

The admin projection is narrower than the neutral surface by design (T-049
dry-run, deltas aligned by T-062): `createService` builds single-layer
Feature-only maps (no styles, descriptions, or multi-service maps — use
`PUT /api/maps` for those), and `uploads` is multipart-only with no
`sourceSrid` reprojection (neutral `/api/ingest` also takes raw bodies and
supports `sourceSrid`). Aligned: `publish` merges the uploaded layer into
the named map like neutral `?publish=` (appends the layer, unions the
Feature service), and `uploads` enforces the same `MaxBytes`/`MaxFeatures`
caps as neutral `/api/ingest`. Neither projection path
pre-validates dataset servability the way `PUT /api/maps` does, and the
error shape differs: the projection returns the Esri envelope (503 when
unconfigured) while the neutral surface returns `{code,message}` with
`invalid.arguments`/`not.found` and leaves mutation routes unmounted
without a token.

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

The `store` query selects `demo` (default, always available), `memory`
(writable, ephemeral, ADR-0042) or `postgis` (needs configuration). Ingest
defaults to `memory` so the database-free upload path works out of the box.

## Configuration

| Key | Meaning |
| --- | --- |
| `Spatial:Postgis:ConnectionString` | PostGIS connection string (empty = unconfigured; every PostGIS call throws `store.unavailable`) |
| `SPATIAL_POSTGIS_CONNECTION` | Env fallback for the connection string — the **only** secret channel |
| `Spatial:WebRoot` | Built workbench directory; when set, `GET /` serves it |
| `Spatial:GeoServices:Root` | GeoServices URL prefix (default `/arcgis/rest/services`) |
| `Spatial:GeoServices:Services` | Logical Esri service `{name, store, type}` entries (`FeatureServer` only); projected to declared Feature maps at composition |
| `Spatial:GeoServices:AdminRoot` | Esri admin projection prefix (default `/arcgis/admin`) |
| `Spatial:Admin:Token` | Admin token for the mutation routes; empty disables them |
| `SPATIAL_ADMIN_TOKEN` | Env fallback for the admin token — a secret channel alongside the connection string |
| `Spatial:Maps:Path` | Runtime map JSON file (default `./data/maps.json`) |
| `Spatial:Maps:LegacyPath` | Pre-ADR-0053 legacy map file (`publications.json` wire shape) read once for migration |
| `Spatial:Maps:Declared` | Config-seeded immutable maps (`{name, store, services[], layers[]}`) |
| `Spatial:Ogc:Root` | OGC WMS/WFS URL prefix (default `/ogc`) |
| `Spatial:Ogc:ServiceTitle` | Capabilities title shared by WMS and WFS |
| `Spatial:Ogc:MaxFeatures` | Largest feature count one WFS `GetFeature` returns |
| `Spatial:Ingest:MaxBytes` / `MaxFeatures` / `Formats` | Ingest caps and the format allowlist (ADR-0041 §6) |
| `Spatial:ArcGisRest:Services` | Remote ArcGIS REST `{name, url}` stores |
| `Spatial:ArcGisRest:Token` | Optional ArcGIS token; host config only, redacted, never in request bodies |
| `Spatial:Logging:Seq:Url` | Seq server URL (ADR-0045); empty/unset leaves the host console-only |
| `SPATIAL_SEQ_URL` | Env fallback for the Seq URL — injected by the Aspire development profile |
| `Spatial:Logging:Seq:ApiKey` | Optional Seq API key for an authenticated Seq instance |
| `SPATIAL_SEQ_API_KEY` | Env fallback for the Seq API key |

## Observability (ADR-0045)

Serilog is the host's logging provider: `Logging:LogLevel` sets the minimum
levels, the console sink is always on, and the Seq sink is added only when
`Spatial:Logging:Seq:Url` (or `SPATIAL_SEQ_URL`) is configured. Every request
is one structured event (`UseSerilogRequestLogging`; 5xx at `Warning`, else
`Information`), startup records one summary event and actionable
config warnings, and `service.name = Spatial.Host` stamps every event.
The WMS/WFS adapter additionally logs one event per OGC operation with the
operation and request parameters, and a rejected operation at `Warning` with
the mapped `ServiceException` code and reason (set
`Logging:LogLevel:Spatial.Adapter.Ogc` to `Warning` to quiet successful
operations).
Diagnostics carry configuration *state* only — never a connection string or
token. In the Aspire development profile `AddSeq` runs the Seq container and
injects its endpoint as `SPATIAL_SEQ_URL`; the host needs no Seq to run
(ADR-0018).

## Clients

- TypeScript SDK: `clients/typescript` (`@spatial/client`) — one method per
  route, wire types generated from OpenAPI (`scripts/generate.mjs`),
  drift-checked in `npm test`, includes the SFBAT decoder.
- .NET SDK: `clients/dotnet/Spatial.Client` — one typed method per route,
  core geometry values in and out, `SpatialClientException` failures.
- `eng/e2e-web.sh` proves the real host end-to-end from the TS SDK.
- `eng/seed.sh` (over `tools/seed/`) fetches real public data, ingests it
  (including a server-side reprojection) and publishes styled feature and map
  services through the neutral admin API — an on-demand realistic dataset.
- `clients/dotnet/Spatial.Cli` is a dependency-free console client of the same
  public API (ADR-0052): datasets, maps/layers/styles and a declarative
  `spatial.json` project file, with GeoServices endpoint output. See `cli.md`.

## Frontend boundary

- Workbench = React 19 + TypeScript + MapLibre; talks **only** to the public
  host API through the TS SDK; engine-neutral app state; no spatial logic.
  Only client-side spatial code: `src/sgeom.ts` (SGEOM → GeoJSON, byte-exact
  codec mirror), `src/click-match.ts` and `src/map-geometry.ts` (pure
  projection/bounds math, no pixel reads). `src/composer.ts` is model and
  MapLibre-style mapping only — it publishes ordered layers, not geometry.
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
