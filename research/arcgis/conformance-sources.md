# Esri GeoServices REST conformance sources (T-012)

Check date: **2026-09-13**. Method mirrors T-011: web research with cited
source URLs, mined client test suites/fixtures, and the recorded corpus in
`research/arcgis/` + `tests/fixtures/arcgis/`. For each candidate test:
request, expected response/assertion, real-client dependency, current engine
behaviour with a `file:line` pointer, and rough effort (S < 1 day, M 1–3 days,
L > 3 days). Serve side = `src/Spatial.Adapter.GeoServices` (+ codec
`src/Spatial.Interop.Esri`); consume side = `src/Spatial.Stores.ArcGisRest`.

Existing narrow proof (do not duplicate): `clients/typescript/test/geoservices-e2e.test.ts`
drives the live host with unmodified `@esri/arcgis-rest-feature-service`
(discover service/layer, attribute+geometry query, getFeature by id,
paging+count via `resultOffset`/`resultRecordCount`/`returnCountOnly`/`orderByFields`,
GeometryServer project, MapServer discovery/drawingInfo/query, image 404).
It covers the happy path of each verb, not the parameter surface below.

## 1. Sources mined

### 1.1 `@esri/arcgis-rest-*` v4.11.0 (devDependencies in `clients/typescript`)
- Package: https://github.com/Esri/arcgis-rest-js — `clients/typescript/node_modules/@esri/arcgis-rest-feature-service/dist/esm/query.js`,
  `query.d.ts`, `helpers.d.ts`; `clients/typescript/node_modules/@esri/arcgis-rest-request/dist/esm/request.js`.
- `IQueryFeaturesOptions.f`: `"json" | "geojson" | "pbf" | "pbf-as-geojson" | "pbf-as-arcgis"` (`query.d.ts`).
  The request library defaults every call to `f=json` (`request.js:43,182,457`).
- `queryAllFeatures` (`query.js:~190-290`, checked 2026-09-13): reads the **layer**
  resource first for `maxRecordCount` (default 2000 when absent), then loops
  `resultOffset`/`resultRecordCount` pages with **`returnExceededLimitFeatures: true`**,
  stopping when `returnedCount < pageSize || !exceededTransferLimit`
  (ArcGIS-JSON flag on the response object; GeoJSON flag under `properties`).
- Allowed query params the client can send (`query.js` allowlist): `where`,
  `objectIds`, `relationParam`, `time`, `distance`, `units`, `outFields`,
  `geometry`, `geometryType`, `spatialRel`, `returnGeometry`,
  `maxAllowableOffset`, `geometryPrecision`, `inSR`, `outSR`, `gdbVersion`,
  `returnDistinctValues`, `returnIdsOnly`, `returnCountOnly`, `returnExtentOnly`,
  `orderByFields`, `groupByFieldsForStatistics`, `outStatistics`, `returnZ`,
  `returnM`, `multipatchOption`, `resultOffset`, `resultRecordCount`,
  `quantizationParameters`, `returnCentroid`, `resultType`, `historicMoment`,
  `returnTrueCurves`, `sqlFormat`, `returnExceededLimitFeatures`, `f`.
  `ISharedQueryOptions.spatialRel` (`helpers.d.ts`): 8 relations
  (`Intersects`, `Contains`, `Crosses`, `EnvelopeIntersects`,
  `IndexIntersects`, `Overlaps`, `Touches`, `Within`) — we accept 2.
- `checkForErrors` (`request.js:131-165`): throws `ArcGISRequestError` on a body
  `error.code >= 400`, and `ArcGISAuthError` on 498/499, **regardless of HTTP
  status**; HTTP error statuses also throw. So envelope shape matters more than
  status for this client (see T10).
- `getAllLayersAndTables` exists in the package and reads both `layers` and
  `tables` off the service root (see T14).

### 1.2 Esri REST API reference (services-reference/enterprise)
- Query (Feature Service layer): https://developers.arcgis.com/rest/services-reference/enterprise/query-feature-service-layer/
  — parameter table confirms `resultOffset`, `resultRecordCount`,
  `returnCountOnly`/`returnIdsOnly`/`returnExtentOnly`/`returnDistinctValues`,
  `orderByFields` (`field1 <ORDER>, …`), `outStatistics` + `having` +
  `groupByFieldsForStatistics` (example `havingClause=COUNT(houses) > …`),
  `quantizationParameters={…}`, `maxRecordCountFactor`, `sqlFormat`,
  `historicMoment`, `geometryPrecision`, `maxAllowableOffset`, `outSR`,
  `spatialRel`, `objectIds`, `time` (instant `time=1199145600000`, extent
  `time=start,end`, `null` = infinity), date-time `TIMESTAMP …` /
  `CURRENT_TIMESTAMP ± INTERVAL` where-syntax, full-text search operators,
  `supportedQueryFormats` (`"JSON,geoJSON,PBF"` example), `f` (**default is
  `html`**, i.e. the Services Directory; `f=pjson` used in examples),
  13 JSON response examples (count, ids, extent, statistics, …).
- Layer (Feature Service): https://developers.arcgis.com/rest/services-reference/enterprise/layer-feature-service/
  — `advancedQueryCapabilities` object pins the flags clients branch on:
  `supportsPagination`, `supportsStatistics`, `supportsOrderBy`,
  `supportsDistinct`, `supportsHavingClause`, `supportsCountDistinct`,
  `supportsPercentileStatistics`, `supportsReturningQueryExtent`,
  `supportsPaginationOnAggregatedQueries`, `supportsQueryWithDistance`,
  `supportsTrueCurve`, `supportsLod`, `supportsQuantization`,
  `supportsFullTextSearch`, `useStandardizedQueries`;
  plus `supportsStatistics`, `supportsAdvancedQueries`, `supportedQueryFormats`,
  `maxRecordCount` vs `standardMaxRecordCount`.
- No `returnExceededLimitFeatures` on the query reference (client convention
  from arcgis-rest-js, §1.1) — confirm-before-honor, see T6.

### 1.3 Koop `@koopjs/featureserver` (serves Esri JSON — same direction as our facade)
- Repo: https://github.com/koopjs/featureserver — `test/unit/query/`:
  `filter-and-transform.spec.js`, `index.spec.js`,
  `render-count-and-extent.spec.js`, `render-features.spec.js`,
  `render-precalculated-statistics.spec.js`, `render-statistics.spec.js`;
  plus `test/unit/layer-metadata.spec.js`, `layers-metadata.spec.js`,
  `server-info-route-handler.spec.js`, `response-handler.spec.js`.
- `render-statistics.spec.js` (checked 2026-09-13) pins the statistics response
  shape: `{displayFieldName, fields, features: [{attributes}]}` — no geometry,
  no `objectIdFieldName`. Direct model for T4's expected body.
- Koop's server-info/statistics/count/extent specs are reusable as
  request→shape fixtures for our adapter tests (input GeoJSON fixtures differ,
  but the asserted Esri envelopes are the conformance target).

### 1.4 pygeoapi Esri provider (consumes ArcGIS REST — same direction as our provider, and a client of our facade)
- File: https://github.com/geopython/pygeoapi/blob/master/pygeoapi/provider/esri.py (checked 2026-09-13).
- **Connect-time gate** (`esri.py:~90-115`): fetches metadata with `f=pjson`,
  then asserts `advancedQueryCapabilities.supportsPagination`,
  `advancedQueryCapabilities.supportsOrderBy`, and `'geoJSON' in
  supportedQueryFormats` — else `ProviderConnectionError`/`ProviderTypeError`.
  Our facade fails all three today (see T1–T3).
- Query mapping (`esri.py:~140-180,340-350`): `f=geoJSON`, `outSR`, `outFields`,
  `where`, `orderByFields` (always sent), `resultOffset`/`resultRecordCount`,
  `geometryType=esriGeometryEnvelope` + `geometry=xmin,ymin,xmax,ymax`
  (4-number simple string, **no `inSR`**), `inSR=4326` for bbox input,
  `returnCountOnly=true` for hits, token via `generateToken` POST
  (`esri.py:216-236`).

### 1.5 GDAL OGR ESRIJSON / FeatureService driver (consumes query responses)
- Docs: https://gdal.org/en/stable/drivers/vector/esrijson.html (checked 2026-09-13).
- Example 1 reads a FeatureService query URL with **`f=pjson`**:
  `…/FeatureServer/0/query?resultRecordCount=10&f=pjson`. Our facade 400s that
  URL today (see T1). No separate "ArcGIS REST driver" exists in GDAL —
  ESRIJSON-over-HTTP **is** the driver surface; long URLs fall back to POST
  (already OK: we accept GET+POST everywhere).

### 1.6 Recorded corpus (`research/arcgis/`, `tests/fixtures/arcgis/captured/`)
- 52 endpoints / 316 layers; behaviours: 230 paged (`exceededTransferLimit`),
  54 empty, 21 null-geometry, 316 error probes; errors `400` bad query and
  `499` token-required. See `research/arcgis/README.md`.
- The corpus exercises the **consume** path (provider replay). It does not
  cover serve-side request shapes (statistics, quantization, time, f variants)
  — the gap this catalogue fills.

## 2. Candidate surface verdicts (from the task body)

| Parameter / flag | Verdict | Test |
|---|---|---|
| `resultOffset`/`resultRecordCount` pagination, `returnCountOnly`, `orderByFields` | supported, e2e-pinned | — |
| `outSR` | supported (serve+consume) | — |
| `objectIds` | supported | — |
| `spatialRel` (8 relations) | partial: 2 of 8 accepted | T7b |
| `outStatistics`/`groupByFieldsForStatistics`/`having` | explicitly rejected 400 | T4 |
| `quantizationParameters` | silently ignored | T8 |
| dates: epoch-ms attributes | supported; `time` param + date where-literals rejected/unsupported | T7 |
| `f=json` | supported; `f=pjson`/`geojson`/`html`/`pbf*` rejected 400 | T1, T2, T15 |
| MapServer `export`/`identify`/`find` | served; `identify` ignores `layerDefs` | T11 |
| ImageServer semantics | routes served; mosaic/`rasterIds` path thin | T13 |
| error/envelope fidelity | shape OK; HTTP status convention differs from ArcGIS Server | T10 |
| capability flags (`supportedQueryFormats`, `advancedQueryCapabilities`, `maxRecordCount`, `supportsStatistics`, `supportsPagination`) | **missing** — blocks pygeoapi | T3 (+T2) |
| `inSR` | silently ignored (wrong-CRS risk) | T5 |
| `returnExceededLimitFeatures`, `maxRecordCountFactor` | silently ignored | T6 |
| `returnZ`/`returnM` | explicitly rejected 400 | keep |
| `tables` in service root | always `[]` | T14 |
| token 498/499 (consume) | mapped to `store.unavailable` by design | T12 (dismiss) |
| `gdbVersion`/`historicMoment`/`sqlFormat`/`resultType`/`datumTransformation`/etc. | silently ignored | T9 (policy) |

## 3. Implementable tests

Process (owner mandate): **test-first** — each item below is specified as a
failing test (protocol request → expected assertion → current wrong behaviour
with pointer). Follow-up tasks must land the red test before the fix.

### T1. `f=pjson` is accepted as a JSON alias (serve)
- Request: `GET /arcgis/rest/services/demo/FeatureServer?f=pjson` and
  `…/0/query?where=1%3D1&outFields=*&f=pjson`.
- Expected: HTTP 200 with byte-identical semantics to `f=json`
  (pretty-printing allowed).
- Real-client dependency: GDAL ESRIJSON driver example queries with
  `f=pjson` (§1.5); pygeoapi fetches metadata with `f=pjson` (§1.4).
- Current behaviour: `EsriFormat.Ensure` throws invalid-arguments → HTTP 400
  for anything but `json` (`src/Spatial.Adapter.GeoServices/EsriJson.cs:40-55`).
- Effort: **S**. Red test: `GeoServicesFormatTests.Pjson_is_accepted_wherever_json_is`
  (root, layer, query, MapServer root, export `f=json`).

### T2. `f=geojson` on query: serve GeoJSON or advertise honestly (serve)
- Request: `GET …/FeatureServer/0/query?where=1%3D1&outFields=*&f=geojson`.
- Expected (option A): HTTP 200 GeoJSON `FeatureCollection` (WGS84 per
  `queryPbfAsGeoJSONOrArcGIS` convention, `outSR` forced to 4326 on conflict);
  (option B, minimal): HTTP 400 whose message names `supportedQueryFormats`
  **and** the layer advertises a truthful `supportedQueryFormats` (see T3).
- Real-client dependency: `IQueryFeaturesOptions.f` includes `geojson`
  (§1.1); pygeoapi requires `'geoJSON' in supportedQueryFormats` and queries
  `f=geoJSON` (§1.4).
- Current behaviour: HTTP 400 from `EsriJson.cs:40-55`; root advertises
  `SupportedQueryFormats = "JSON"` only
  (`src/Spatial.Adapter.GeoServices/FeatureService.cs:Root`).
- Effort: **S** for option B, **M** for option A. Red test first either way.

### T3. Layer + root advertise `advancedQueryCapabilities` truthfully (serve)
- Request: `GET …/FeatureServer/0?f=json` (and service root).
- Expected: `advancedQueryCapabilities` containing at least
  `{supportsPagination: true, supportsOrderBy: true, supportsStatistics: false,
  supportsDistinct: <per returnDistinctValues>, supportsHavingClause: false,
  supportsReturningQueryExtent: <per returnExtentOnly>, useStandardizedQueries: false}`
  matching actual engine behaviour; `supportsPagination: true` only if paging
  is honoured as implemented (`FeatureQueryEngine.cs:288-294`).
- Real-client dependency: pygeoapi connect gate asserts `supportsPagination`
  + `supportsOrderBy` (§1.4) — unmodified pygeoapi cannot use our facade today.
- Current behaviour: `EsriLayerModel.Describe`
  (`src/Spatial.Adapter.GeoServices/EsriLayerModel.cs:55-80`) emits no
  `advancedQueryCapabilities`, no `supportsStatistics`/`supportsPagination`,
  and a bare `maxRecordCount: 1000` (`EsriLayerModel.cs:26`).
- Effort: **S-M** (record shape + per-flag tests; every flag must be proved by
  the behaviour test it names). Red test: `…/0?f=json` contains the object with
  values cross-checked against T4/T6 outcomes.

### T4. `outStatistics` + `groupByFieldsForStatistics` + `having` (serve)
- Request: `GET …/0/query?where=1%3D1&outStatistics=[{"statisticType":"sum","onStatisticField":"population","outStatisticFieldName":"sumpop"}]&groupByFieldsForStatistics=region&f=json`.
- Expected: HTTP 200 `{displayFieldName, fields, features: [{attributes: …}]}`
  per Koop `render-statistics.spec.js` (§1.3); `having` filters groups
  (Esri `havingClause=COUNT(…) > …` example, §1.2); stats on empty set → one
  row of nulls (Esri response example 5).
- Real-client dependency: `IQueryFeaturesOptions.outStatistics` (§1.1);
  Koop statistics specs as shape fixtures (§1.3).
- Current behaviour: explicit HTTP 400
  (`src/Spatial.Adapter.GeoServices/EsriFeatureQuery.cs:205-211`).
- Effort: **L** (aggregation over the scan path + statistic-type table
  `count|sum|min|max|avg|stddev|var`; start with `count/sum/min/max/avg` +
  `having` on the same functions). Red tests: one per statistic type +
  grouped + having + empty-set, mirroring Koop's spec names.

### T5. `inSR` is honoured for query geometry (serve) — silent-wrong-CRS today
- Request (against a 3857 layer): `…/0/query?geometry=<4326 envelope>&geometryType=esriGeometryEnvelope&inSR=4326&spatialRel=esriSpatialRelEnvelopeIntersects&f=json`.
- Expected: geometry interpreted in `inSR`, transformed to the layer CRS
  before `FeatureQueryEngine.Matches` (same path as
  `TransformQueryGeometry`, `FeatureQueryEngine.cs:200-213`).
- Real-client dependency: `ISharedQueryOptions.inSR` (§1.1); any client whose
  map CRS differs from the layer CRS (pygeoapi sends `inSR=4326`, §1.4).
- Current behaviour: `inSR` is never read — `EsriFeatureQuery.Parse` passes
  the **layer** CRS as the geometry fallback
  (`src/Spatial.Adapter.GeoServices/EsriFeatureQuery.cs:36-70`), so a 4326
  envelope against a 3857 layer is silently evaluated in the wrong CRS.
- Effort: **S-M**. Red test: 4326 envelope over Berlin returns Berlin on a
  3857 layer only when `inSR=4326` is honoured (and unchanged behaviour when
  absent).

### T6. `returnExceededLimitFeatures` + `maxRecordCountFactor` (serve)
- Request (exactly what REST JS sends, §1.1): layer metadata read for
  `maxRecordCount`, then `…/query?…&returnExceededLimitFeatures=true&resultOffset=N&resultRecordCount=<maxRecordCount>`.
- Expected: HTTP 200; `returnExceededLimitFeatures=true` either honoured
  (return the over-limit features flagged) or explicitly accepted-and-ignored
  with `exceededTransferLimit` still correct; `maxRecordCountFactor=k`
  raises the effective page cap to `k × maxRecordCount` (Esri reference, §1.2).
- Real-client dependency: `queryAllFeatures` always sends
  `returnExceededLimitFeatures: true` and pages off `maxRecordCount` (§1.1).
- Current behaviour: both params silently ignored — no field on the
  `EsriFeatureQuery` record and no branch in `Page`
  (`EsriFeatureQuery.cs:27-70`, `FeatureQueryEngine.cs:288-294`);
  `maxRecordCount` is a fixed 1000 (`EsriLayerModel.cs:26`).
- Effort: **S** (accept-and-ignore + test) to **M** (honour the factor).
  Red test: full `queryAllFeatures` loop against a >1000-feature layer via the
  real JS client terminates with all features.

### T7. Temporal surface: `time` + date literals in `where` (serve)
- Request: `…/0/query?time=1199145600000&f=json`; and
  `where=created_date = TIMESTAMP '2024-01-01 00:00:00'` /
  `where=<datefield> >= CURRENT_TIMESTAMP - INTERVAL 1 DAY` (§1.2 date-time queries).
- Expected: temporal filtering against `esriFieldTypeDate` fields, or a
  truthful capability signal (`timeInfo` absent + documented reject).
- Real-client dependency: `ISharedQueryOptions.time` (§1.1); time-aware web
  maps and dashboards send `time` on every query.
- Current behaviour: `time` explicitly rejected 400 (`EsriFeatureQuery.cs:207`);
  `EsriFilterClause` supports only AND/OR/parens/comparisons/LIKE/IS NULL
  (`src/Spatial.Interop.Esri/EsriFilterClause.cs:9,16-70`) — no date literals.
  Attribute codec already round-trips epoch-ms dates
  (`src/Spatial.Interop.Esri/EsriAttributeCodec.cs:44,128-130`).
- Effort: **M-L**. Red tests: `time` instant, `time` extent with `null`
  infinity bound, `TIMESTAMP` literal, `CURRENT_TIMESTAMP ± INTERVAL`.

### T7b. Remaining `spatialRel` values (serve)
- Request: `…/0/query?geometry=…&geometryType=esriGeometryPolygon&spatialRel=esriSpatialRelContains` (and `Within`, `Touches`, `Overlaps`, `Crosses`).
- Expected: exact-geometry predicate via `IGeometryOperations` verbs instead
  of the envelope test (the `Intersects` path in
  `FeatureQueryEngine.cs:180-198` is the model).
- Real-client dependency: `SpatialRelationship` union of 8 (§1.1); only
  `EnvelopeIntersects` + `Intersects` accepted
  (`EsriFeatureQuery.cs:120-135`).
- Current behaviour: HTTP 400 for the other six.
- Effort: **M** (predicate per relation + tests; `IndexIntersects` may stay
  rejected — document it).

### T8. `quantizationParameters` / `geometryPrecision` / `maxAllowableOffset` (serve)
- Request: `…/0/query?where=1%3D1&returnGeometry=true&quantizationParameters={"mode":"view","originPosition":"upperLeft","tolerance":1.09,"extent":{…}}&f=json`.
- Expected: quantized geometry response per the Esri reference (§1.2), or an
  explicit 400 naming the unsupported parameter.
- Real-client dependency: tiled web-map query path sends quantization params
  (`IQueryFeaturesOptions`, §1.1); `supportsQuantization` flag (§1.2).
- Current behaviour: silently ignored — no quantization path exists anywhere
  in `src/`; full-precision coordinates are returned as if requested.
- Effort: **M** to implement, **S** to explicitly reject. Red test first;
  rejecting silently-wrong output is the acceptable minimal fix.

### T9. Silent-ignore audit: `sqlFormat`, `resultType`, `gdbVersion`, `historicMoment`, `datumTransformation`, `returnCentroid`, `distance`/`units`, `relationParam`, `text` (serve)
- Request: each of `…/0/query?sqlFormat=standard`, `?resultType=tile`,
  `?distance=100&units=esriSRUnit_Meter`, `?text=broken+pipe`, etc.
- Expected: each parameter is either honoured or explicitly rejected with a
  named message (the `RejectUnsupported` policy at
  `EsriFeatureQuery.cs:205-217`) — never silently dropped where dropping
  changes semantics (`distance`+`units` without effect returns wrong rows).
- Real-client dependency: all are in the REST JS send-allowlist (§1.1);
  full-text `text` is a documented Esri query mode (§1.2).
- Current behaviour: none are read; `text` full-text search has no engine path.
- Effort: **S** for the reject-list + tests; `text`→LIKE mapping is **M** if
  pursued. Red test: parameterised theory asserting reject-or-honour per name.

### T10. Error envelope + HTTP status fidelity (serve)
- Request: `…/0/query?where=<garbage>&f=json`; `GET …/FeatureServer/999?f=json`;
  cancelled request.
- Expected (to confirm against ArcGIS Server): ArcGIS Server returns **HTTP
  200 + `{"error": {code, message, details}}`** for query-level failures in
  the default configuration; our facade returns HTTP 400/404/500 with the
  envelope (`src/Spatial.Adapter.GeoServices/EsriJson.cs:57-135`,
  `HttpFor` at `:117`). `details` should be `[]` not absent (Esri examples
  always carry the key; ours drops nulls via `WhenWritingNull`).
- Real-client dependency: `checkForErrors` handles both conventions (§1.1),
  but GDAL/QGIS/service-mesh HTTP handling keys off status — pick the
  convention deliberately and pin it.
- Current behaviour: envelope shape right, status convention unpinned by any
  test; `details` omitted when null.
- Effort: **S-M** (recorded-fixture test matrix: bad-where, unknown
  layer/service, bad-f, cancelled; assert status+envelope per case).

### T11. MapServer `identify` honours `layerDefs` (serve)
- Request: `…/MapServer/identify?geometry=<point>&geometryType=esriGeometryPoint&sr=4326&layers=all&layerDefs={"0":"population > 1000000"}&mapExtent=…&imageDisplay=…&tolerance=3&f=json`.
- Expected: only features satisfying the layer definition are returned
  (spec §4.0.5 lists `layerDefs` on identify).
- Real-client dependency: ArcGIS Maps SDK identify task forwards layer
  definitions; export already parses them
  (`MapRenderEngine.ParseLayerDefs`, used in `GeoServicesEndpoints.MapExport.cs:47`).
- Current behaviour: `MapIdentifyEngine.IdentifyAsync`
  (`src/Spatial.Adapter.GeoServices/MapIdentifyEngine.cs:17-35`) reads
  `sr`/`geometry`/tolerance/`layers`/`returnGeometry` only — `layerDefs` is
  silently ignored, so identify can return features the map would not draw.
- Effort: **S-M** (reuse the export `layerDefs` path; red test: identify with
  a restrictive `layerDefs` returns the subset).

### T12. Consume: 498/499 token errors stay `store.unavailable` (dismiss by design)
- Request (provider test): mock ArcGIS REST returning
  `{"error": {"code": 499, "message": "Token required"}}`.
- Expected: `SpatialException` with code `store.unavailable` — there is no
  auth-error code in the engine taxonomy, and the provider takes a static
  token (`ArcGisRestStore.BuildUrl` appends `token=`).
- Real-client dependency: ArcGIS Online token flow (pygeoapi `generateToken`,
  §1.4); corpus already records `499` services.
- Current behaviour: `ArcGisRestMapper.MapError`
  (`src/Spatial.Stores.ArcGisRest/ArcGisRestMapper.cs:99-113`) maps 400→
  `invalid.arguments`, 404→`not.found`, everything else → `store.unavailable`;
  pinned by `A_token_required_error_maps_to_store_unavailable`.
- Effort: **—** (no change; keep the characterisation test).

### T13. Consume + serve: ordered paging and ImageServer query surface
- Provider request: today's `QueryParameters`
  (`src/Spatial.Stores.ArcGisRest/ArcGisRestStore.cs:QueryParameters`)
  sends `resultOffset`/`resultRecordCount` with **no `orderByFields`** — a
  remote that does not stabilise order can overlap/drop rows across pages.
  pygeoapi always sends `orderByFields` (§1.4). Fix: send
  `orderByFields=<objectIdField>` when the metadata names one; red test with a
  mock handler asserting the parameter.
- ImageServer routes (`exportImage`, `identify`, `query`, `download`, `file`,
  raster resource) are registered
  (`src/Spatial.Adapter.GeoServices/GeoServicesEndpoints.Images.cs:26-52`)
  and catalog query shares `FeatureQueryEngine.Project`, but
  `mosaicRule`/`renderingRule`/`bandIds` handling is thin — file one
  reconnaissance test per operation recording current behaviour before
  extending.
- Effort: **S** (orderBy) + **S** (reconnaissance tests).

### T14. Service root exposes `tables` (serve)
- Request: `GET …/FeatureServer?f=json` on a store with non-spatial datasets.
- Expected: non-spatial datasets appear under `tables`, not `layers`
  (the corpus has 23 tables; the provider already distinguishes them).
- Real-client dependency: `getAllLayersAndTables` reads both arrays (§1.1).
- Current behaviour: `FeatureService.Root` always emits `tables: []`
  (`src/Spatial.Adapter.GeoServices/FeatureService.cs:Root`).
- Effort: **S**. Red test: mixed store → tables listed with stable ids.

### T15. `f=html` Services Directory (dismiss)
- Request: `GET …/FeatureServer` with no `f` from a browser (Esri default
  `f` is `html`, §1.2).
- Verdict: keep rejecting — this facade is a JSON API surface by design
  (ADR-0035); a human-browsable directory is a separate concern. Pin the
  rejection message to name the supported value. No follow-up task.
- Effort: **—**.

## 4. Follow-ups spawned

High-value items spawned with `eng/tasks add --area interop.esri` (see
`eng/tasks list`); each requires the red test before the fix:
T1→T-016, T2→T-017, T3→T-018, T4→T-019, T5→T-020, T6→T-021, T7→T-022,
T7b+T8→T-023, T9→T-024, T10→T-025, T11→T-026, T13→T-027, T14→T-028.
T12/T15 are dismissals recorded here, no tasks.
