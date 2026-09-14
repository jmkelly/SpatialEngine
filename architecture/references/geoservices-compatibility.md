# GeoServices REST Spec v1.0 ↔ SpatialEngine compatibility review

> **Status:** baseline review completed 2026-09-11. The follow-on
> decisions live in
> `architecture/decisions/ADR-0035-geoservices-rest-boundary-adapter.md`.
> This document remains the gap analysis that decision builds on.

Sources: `architecture/references/geoservices-rest-spec.pdf` (Esri, Sept
2010, 221 pp, Open Web Foundation Agreement) and the current ArcGIS REST
API online documentation, checked 2026-09-11:

- Feature Service — https://developers.arcgis.com/rest/services-reference/enterprise/feature-service/
- Layer query — https://developers.arcgis.com/rest/services-reference/enterprise/query-feature-service-layer/
- `addFeatures` — https://developers.arcgis.com/rest/services-reference/enterprise/add-features/
- `updateFeatures` — https://developers.arcgis.com/rest/services-reference/enterprise/update-features/
- `deleteFeatures` — https://developers.arcgis.com/rest/services-reference/enterprise/delete-features/
- `applyEdits` — https://developers.arcgis.com/rest/services-reference/enterprise/apply-edits/
- Geometry Service — https://developers.arcgis.com/rest/services-reference/enterprise/geometry-service/

The current engine state is the authored code in `src/` plus `README.md`,
`architecture/distilled/*`, ADRs 0001/0005/0020/0033/0035/0036/0037,
`src/Spatial.Host/Api`, `src/Spatial.PluginSdk/Http`,
`IGeometryOperations`. Review date: 2026-09-11; editing addendum: 2026-09-13.

## Verdict

**Structurally compatible at the value-model level; wire- and
capability-incompatible today.** The spec is a REST *serving* contract
(GET + `f=json`, Esri JSON geometries/features, WKID spatial references);
the engine is a typed in-process *engine* exposed over a small JSON POST
API carrying canonical binary (`SGEOM`/`SFBAT`). Nothing here is
unbridgeable, but today roughly **3 of 19 Geometry Service operations map
(and one of those has conflicting semantics)** and **1 of 6 Feature
Service operations has an analogue** (append-only, different shape).

Two directions must not be confused:

- **Consuming** ArcGIS REST (engine as a data provider) — low friction,
  already anticipated as a provider in ADR-0035. This is the natural fit.
- **Serving** GeoServices (engine answers ArcGIS clients) — a facade
  commitment that collides with ADR-0020 and the query-security model, and
  needs a large verb expansion. Needs its own ADR.

## 1. Protocol and interchange

| Dimension | GeoServices v1.0 | SpatialEngine | Compatible |
| --- | --- | --- | --- |
| Method | GET with query params (`f=json` required; `f=pjson` accepted as a JSON alias; POST only for edits) | POST JSON bodies, camelCase, OpenAPI | No |
| Service root | `<catalog>/<serviceName>/<Map\|Feature\|Geometry\|…>Server` | `/api/...` route groups; root is an identity doc | No (mappable) |
| Geometry wire | JSON `{x,y}` / `paths` / `rings` / `points` / `{xmin..}` | Base64 `SGEOM` (ADR-0020) | No |
| Feature wire | JSON `{geometry, attributes}` inline | Base64 `SFBAT` batch pages | No |
| Spatial reference | `{wkid: 4326}` or `{wkt: "GEOGCS[...]"}` | `"EPSG:4326"` string identity (ADR-0009) | No; mappable for EPSG, WKT absent |
| Feature identity | `objectId` int (+ `globalId`) | `FeatureId` non-empty string | No; mappable with care |
| Edit result | `{objectId, globalId, success, error:{code,description}}` | `{appended: n}` or typed `ErrorResponse` | No |
| Error shape | Per-edit `error{code,description}`; no global envelope | `{code,message}` + HTTP 400/404/503/499 (`runtime.md`) | No; mappable |
| Long work | GP `submitJob` + `job` polling (stateful-ish) | Cancellable request; **no job model** (ADR-0033) | No |
| Transactions | "stateless because REST" (§2.0); `applyEdits` is the atomic unit | Explicit `begin/commit/rollback` handles | Different model |

Axis order aligns: GeoServices geometry arrays are x-first, and the
engine is x-first for every CRS (`contracts.md`).

## 2. Geometry Service (spec §7)

19 operations. `IGeometryOperations` ships four verbs
(`Buffer`, `Intersection`, `Validate`, `Simplify`).

| GeoServices op | Engine | Status |
| --- | --- | --- |
| `project` | `ICoordinateTransforms.Transform` | **Partial** — one geometry vs array; `inSR`/`outSR` wkid vs `source`/`target` `EPSG:` string |
| `simplify` (topological repair; §7.0.5) | `Validate` only | **Missing** — engine has no MakeValid/repair |
| `buffer` | `IGeometryOperations.Buffer` + `ICoordinateTransforms` | **Partial** — planar transform-then-buffer with `unit` (curated table), `bufferSR`/`outSR`/`inSR` chaining, multi-`distances`, `quadrantSegments`; `geodesic`/`unionResults` honestly rejected, linear units need a projected buffer CRS |
| `areasAndLengths` | — | Missing (core excludes area/length, `core.md`) |
| `lengths` | — | Missing (same) |
| `relation` (DE-9IM `relationParam`) | — | Missing |
| `labelPoints` | — | Missing |
| `distance` | — | Missing |
| `densify` | — | Missing |
| `generalize` (Douglas-Peucker; §7.0.13) | `Simplify` | **Present, wrong name** |
| `convexHull` | — | Missing |
| `offset` | — | Missing |
| `trimExtend` | — | Missing |
| `autoComplete` | — | Missing |
| `cut` | — | Missing |
| `difference` | — | Missing |
| `intersect` | `Intersection` | **Partial** — `(array, geometry)` vs `(left, right)`; engine disjoint→empty (spec-compatible) |
| `reshape` | — | Missing |
| `union` | — | Missing (`Buffer.unionResults` is not the op) |

**Semantic trap:** the engine's `Simplify` is Douglas-Peucker
(`contracts.md`, `README.md`), which is GeoServices **`generalize`**.
GeoServices **`simplify`** repairs self-intersections/overlapping rings
(§7.0.5.3 example: one ring → two rings). Mapping by name silently
produces the wrong result.

**Buffer semantics trap:** GeoServices applies a `unit`, can buffer in a
third CRS (`bufferSR`) and geodesically for points/multipoints in a
geographic CRS. The engine buffer is planar transform-then-buffer: a
`distances=1000` + `unit=9001` request against a 4326 geometry with a
projected `bufferSR` reproduces the projected result, but a linear `unit`
against a geographic buffer CRS stays rejected (no geodesic verb) and an
angular `unit` buffers planar degrees.

## 3. Feature Service (spec §9)

| GeoServices resource/op | Engine | Status |
| --- | --- | --- |
| Catalog (§3): folders + `services[{name,type}]` | `GET /api/catalogue`: spatial `DatasetSummary[]` | Different concept |
| `FeatureServer` root: `layers[]`, `tables[]` (§9.0) | — | **Served** — spatial datasets under `layers`, datasets without a geometry field under `tables` (stable ids from the single id space; table metadata reports `type: Table` and supports the safe query subset) |
| Layer metadata (§9.1): fields, `geometryType`, `objectIdField`, `drawingInfo`, `templates`, `capabilities`, relationships, `timeInfo`, `hasAttachments` | `GET /api/datasets/{id}`: fields, geometry column/SRID/type, identity columns | Partial; different JSON |
| `query` (§9.1.4): `objectIds`, `where`, `geometry`+`geometryType`+`spatialRel`, `outFields`, `returnGeometry`, `outSR`, `returnIdsOnly`, `resultOffset`/`resultRecordCount`, `returnCountOnly`, `returnExtentOnly`, `returnDistinctValues`, `time` | `POST /api/features/query`: `dataset`, `bbox`, `filter` | **Partial** — bbox ≈ envelope-intersects; the facade implements `where` (closed grammar, including `TIMESTAMP`/`CURRENT_TIMESTAMP ± INTERVAL` date literals), field projection, `outSR`, paging, ids/count/extent-only and distinct values, and `time` (instant or start,end extent with `null` infinity bounds, filtered against the layer's date fields and ignored when the layer has none); `EnvelopeIntersects`/`Intersects`/`Contains`/`Within`/`Touches`/`Overlaps`/`Crosses` are served, only `esriSpatialRelIndexIntersects` stays rejected (it names an index optimisation, not a predicate). The service-level `FeatureServer/query` (S1) fans the same subset across layers and tables with `layerDefs` (all three syntaxes) and returns one feature set, count, or id list per layer (ADR-0061); layer-only shapes (extent, distinct, statistics, unique ids) name the layer route instead |
| `generateRenderer` (S4, feature-service layer) | — | **Served** — the single T-039 classifier (`MapGenerateRenderer`, ADR-0055) reused on the FeatureServer surface; byte-identical renderers on both surfaces (ADR-0061) |
| `validateSQL` (S4, feature-service layer) | — | **Served** — server-side WHERE validation returning the S4 `isValidSQL` shape with 3001/3002/3008 codes; `expression`/`statement` validate as not-supported, never run (ADR-0061) |
| `queryBins` / `queryTopFeatures` / `queryAnalytic` (S4) | — | Honestly rejected — mounted typed `invalid.arguments` naming the served alternative (`outStatistics`+`groupByFieldsForStatistics`; `orderByFields`+`resultRecordCount`); unadvertised (ADR-0061) |
| `queryRelatedRecords` (§9.1.5) | — | Missing (no relationship model) |
| `addFeatures` (§9.1.6) | `POST /api/features/write` | Partial — append-only through the API; the GeoServices facade now maps `addFeatures` onto `IFeatureEditStore.AddAsync` (ADR-0037) |
| `updateFeatures` (§9.1.7) | — | Implemented via `IFeatureEditStore.UpdateAsync` (ADR-0037), identity-backed layers only |
| `deleteFeatures` (§9.1.8) | — | Implemented via `IFeatureEditStore.DeleteAsync` (ADR-0037) |
| `applyEdits` (§9.1.9) | transactions (`begin`/`commit`/`rollback`) + write | Implemented at layer level; `rollbackOnFailure` maps to `ITransactionStore`; service-level `applyEdits` out of scope |
| attachments (§9.2–9.6) | — | **Served on the `IFeatureAttachmentStore` capability** (ADR-0065/ADR-0066): layers whose store exposes the capability advertise `hasAttachments` with `attachmentProperties` (id, name, size, contentType, keywords); `queryAttachments` returns per-feature `attachmentGroups`, the per-feature `attachments` resource its `attachmentInfos`, and a per-attachment resource serves the bytes; `addAttachment`/`updateAttachment` (multipart `attachment` part) and `deleteAttachments` (per-id results) mutate behind the single admin token. Stores without the capability (demo, PostGIS until its sidecar lands) keep the honest surface: `hasAttachments: false`, empty reads, typed `invalid.arguments` writes | 
| `htmlPopup`, `image` (§9.2–9.6) | — | Non-goals (§7.1) |

Result-shape additions: `returnExtentOnly` returns the envelope of the full
matched set (computed before paging) in `outSR` or the layer SR, or
`"extent": null` when nothing matches; `returnDistinctValues` returns the
deduplicated projection of `outFields` (all non-geometry fields when
absent) with no geometry, paged after dedupe. `returnIdsOnly`,
`returnCountOnly`, `returnExtentOnly` and `returnDistinctValues` are
mutually exclusive — combining any two is a typed `invalid.arguments`
failure — and unknown distinct `outFields` are rejected rather than silently
widened.

Field types: GeoServices `esriFieldType*` (OID, string, integer, double,
date-as-epoch-ms, geometry) vs engine `AttributeKind`
(`Boolean/Int64/Double/String/Geometry/DateTimeOffset/Guid`,
`core.md`). Values map; the engine model is a superset but the wire names
and date encoding differ.

## 4. Other service types

| Service | Spec | Engine |
| --- | --- | --- |
| Map Service (§4): export, identify, find, tiles, layer query, image | `/arcgis/rest/services/{service}/MapServer` (ADR-0048) | **Implemented** as a projection of a `PublicationKind.Map` publication over the SDK render/tile contracts: root/layers/layer/query/identify/find, `export` (png/jpg/webp/tiff) and Web-Mercator tiles, with a `simple`/`uniqueValue`/`classBreaks` `drawingInfo`, `labelingInfo` and `domains` derived from the persisted MapLibre fragment (ADR-0050). `legend`, `queryDomains` and `queryLegends` project the same persisted style (ADR-0055), and each layer serves `generateRenderer` (equal-interval `classBreaksDef`, single-field `uniqueValueDef`). `export` honours `time`/`timeRelation`/`layerTimeOptions` (per-layer opt-out, cumulative display; non-zero offsets rejected), `dynamicLayers` (mapLayer rebind plus a `drawingInfo` override over the projected renderer subset) and `layerOption` (`all`/`visible`/`top`), and the root advertises `supportsTimeRelation`, `supportsDynamicLayers`, `singleFusedMapCache`+`tileInfo` per served scheme and `exportTilesAllowed:false` (ADR-0058). The offline/async surface — `exportTiles`+`estimateExportTileSize` (map + image variants), the WMTS triple, `generateKml`/the `kml` image, async `jobs` — is mounted and rejected by name with typed `invalid.arguments` pointing at the live alternative (ADR-0060; no packaging or job model). Vector tiles (MVT/`.vtpk`, `VectorTileServer`) and OGC API Tiles stay unserved non-goals: no MVT encoder, no packaging model, no TileJSON (ADR-0062). The image (§4.7) resource is mounted and returns a typed `not.found`: it exists only for picture marker/fill symbols, which the engine's dialect has no model for |
| Geocode Service (§5) | — | Absent |
| GP Service (§6): tasks, `submitJob`, job polling, results | — | Absent; no job model (ADR-0033 removed jobs) |
| Image Service (§8): export, raster functions, download | `/arcgis/rest/services/{service}/ImageServer` (ADR-0051) | **Implemented** as a projection of a `PublicationKind.Image` publication over the SDK `IRasterCatalogue` contract: root metadata (extent, pixel size, band count, pixel type, service data type, catalog fields/objectIdField), raster info, catalog item/listing and §8.0.5 `query` (the safe `where` subset, `objectIds`, geometry, `outFields`, ordering, paging, ids/count/extent/distinct and `outSR`), identify and `exportImage` (png/jpg/tiff, `f=image` bytes or JSON `href`, bbox/image SR, interpolation, compression, pixelType, noData), plus the §8.2 Raster Image, §8.3 Thumbnail, §8.0.7 Download Rasters and §8.5 Raster File resources (opt-in via `Spatial:GeoServices:AllowRasterDownload`, size/file-capped, opaque provider file ids, range-capable file streaming). Tiled/pyramidal GeoTIFFs (COG) report their block size and pyramid levels and export from the chosen overview. Download clipping/re-encoding and raster functions remain absent (I5/I6); provider-owned rasters keep encoded images + core-typed metadata/geometry on the wire |
| Geometry objects (§10) | point/polyline/polygon/envelope, Z/M absent | Superset — engine also has multipoint, multi-\*, geometry collection, Z/M |
| Symbol/renderer/label/domain objects (§12–15) | Map-render oriented | **Projected** from the persisted MapLibre fragment and the catalogue schema (ADR-0050): `esriSFS`/`esriSLS`/`esriSMS` symbols, `simple`/`uniqueValue`/`classBreaks` renderers, the single-field `esriTS` label subset, and coded-value/range domains. Picture symbols (`esriPMS`/`esriPFS`) remain absent — the §4.7 image resource is a typed `not.found` |

## 5. Architectural fit

A GeoServices **adapter is an implementation, not a core change** — DI
composition (ADR-0033) allows a new route group / module without touching
`Spatial.Core`. That part is clean. Three standing decisions bite:

1. **ADR-0020 / `host-and-clients.md`:** "Feature data never crosses as
   JSON geometry." Every GeoServices route does exactly that. Serving it
   requires either an explicit exception for adapter-owned routes or an
   ADR re-framing: canonical binary is the *engine* interchange, while a
   foreign-protocol adapter legitimately owns a second JSON codec at its
   edge.
2. **Query security (`host-and-clients.md`, `contracts.md`):**
   client text never becomes SQL structure; strict identifier grammar +
   bound parameters. GeoServices `where` is an arbitrary SQL WHERE
   clause. It cannot be passed through — a facade must translate a safe
   subset or reject it.
3. **CRS identity (ADR-0009):** Esri WKIDs are not EPSG codes
   (spec example uses **102113**, Web Mercator; modern Esri uses 102100;
   EPSG is 3857). The curated catalogue is 15 EPSG CRSs and no WKT. A
   facade needs a WKID↔EPSG map (and a WKT decision).

The plan already positions ArcGIS REST as a **provider** (§5 architecture
diagram), i.e. the consume direction is the one the architecture
anticipated.

## 6. Concrete work items, if serving is pursued

Ordered by dependency:

1. Esri geometry JSON codec (x/y, `paths`, `rings`, `points`, `xmin..`,
   plus the simple comma syntax and `{"url": ...}` form — the latter is an
   SSRF concern and should be rejected).
2. WKID↔EPSG mapping; decide whether to accept `{wkt}`.
3. Additional verbs: `generalize` (reuse `Simplify`), `Simplify`-as-repair
   (MakeValid), `union`/`difference`, area/length, distance, densify,
   convex hull, offset, plus predicates for `spatialRel`.
4. Array/parameter conventions and `outFields`/`returnGeometry`/`outSR`
   projection (engine query has none today).
5. A safe `where` subset (translate to the existing parameterised filter,
   or reject).
6. Edit results + `addFeatures`/`updateFeatures`/`deleteFeatures`/
   `applyEdits` (needs update/delete verbs the store contracts lack) —
   **delivered** as ADR-0037's additive `IFeatureEditStore`, with
   `{objectId, globalId, success, error:{code,description}}` results.
7. Service/layer metadata JSON (`FeatureServer` root, layer `fields`).
8. Esri error envelope and per-edit `error{code,description}` mapping.

## 7. Recommendation

- If the goal is **ingesting Esri data**: implement ArcGIS REST as a
  `Spatial.Provider.*` over the existing `IDataCatalogue`/`IFeatureStore`
  contracts. Keep the JSON in the provider (ADR-0005 spirit), translate to
  core geometry there, and push down the supported query subset. Minimal
  architectural friction; matches §5.
- If the goal is **emitting GeoServices**: scope a facade deliberately —
  start with a read-only **Geometry Service** subset plus Feature Service
  `query`, against a named target version. Decide **v1.0 vs 10.x** first:
  this PDF is v1.0 (2010); real clients (ArcGIS JS 4.x) expect 10.1+ /
  later (FeatureServer edits, `orderByFields`, `resultOffset`,
  `returnCountOnly`, `hasZ/hasM`, time zones). Record the decision and the
  ADR-0020/query-security exceptions in an ADR before implementing, and
  budget the verb expansion (items 3–6 above) as the dominant cost.
- Serving status update: the 10.x `orderByFields` delta is implemented for
  the Feature Service `query`. It accepts a comma-separated list of
  `fieldName [ASC|DESC]` entries, validated against the layer schema in the
  adapter, and orders the matched features in memory before
  `resultOffset`/`resultRecordCount` — it is never rendered as SQL. Unknown
  fields, geometry fields and malformed entries fail `invalid.arguments`
  (HTTP 400).
- Real-client proof: the e2e suite in `clients/typescript/test/geoservices-e2e.test.ts`
  drives the live host with the official **ArcGIS REST JS** libraries (what
  ArcGIS Maps SDK for JS uses), and closed three real gaps against it:
  resource reads over **POST** (`getService`/`getLayer` POST rather than GET,
  per spec §2.0.1), the Esri **match-all** `where=1=1` predicate (and the
  general constant predicate), and the **Feature (object) resource**
  `FeatureServer/<layerId>/<objectId>` that `getFeature` reads.
- Recorded Geometry Service non-goals: `offset`, `cut`, `reshape`,
  `trimExtend` and `autoComplete` have no engine verb; the facade rejects
  them with a typed `invalid.arguments` failure and does not advertise them.
- Serving status update (T-044): `buffer` honours `unit` (curated linear +
  angular code table, factors verified against the hosted service) with
  `bufferSR`/`outSR`/`inSR` chaining per spec §7.0.6 via
  transform-then-buffer; `geodesic=false` is accepted as planar while
  `geodesic=true`/`unionResults` stay rejected. `findTransformations`
  honestly lists the curated catalogue path (same datum → `[]`, datum step →
  one forward composite with the OSGB36 classic-Helmert note).
  `fromGeoCoordinateString`/`toGeoCoordinateString` are recorded non-goals
  (§7.1): rejected by name, unadvertised.
- Serving status update: `f=pjson` is accepted as a JSON alias everywhere
  `f=json` is (GDAL ESRIJSON driver, pygeoapi metadata fetch); `f=geojson`
  on query is honestly rejected with a typed `invalid.arguments` failure
  naming `supportedQueryFormats` — GeoJSON output remains a non-goal.
- Serving status update: the FeatureServer root exposes `tables`. Datasets whose schema carries no geometry field are listed under `tables` (what `getAllLayersAndTables` reads) with stable ids from the single layer/table id space; their metadata reports `type: Table` and serves the safe `where`/`objectIds` query subset (geometry filters match nothing — there is nothing to intersect).
- Serving status update: MapServer `identify` honours `layerDefs`. Each entry parses with the shared safe where-grammar (the same path as `export`) and filters that layer's candidates — including the synthetic `OBJECTID` resolved exactly as the query path does — so identify can no longer return features the map would not draw. A malformed `layerDefs` value is a typed `invalid.arguments` failure.
- Serving status update: the error envelope + HTTP status convention is pinned. Query-level failures return typed HTTP statuses consistent with the engine mapping (400 invalid parameters, 404 not found, 401/403 token failures, 499 cancelled, 503 store unavailable, 500 server error) rather than ArcGIS Server's 200-by-default: arcgis-rest-js branches on the envelope <c>error.code</c> either way, while HTTP-aware clients (GDAL/QGIS/service meshes) key off status. Every envelope carries a <c>details</c> array (empty when there is nothing to add), matching the Esri examples.
- Serving status update: the temporal surface is served. The `time` query parameter accepts an instant (`time=ms`) or an extent (`time=start,end`, either bound `null` for infinity); bounds are epoch milliseconds (ISO-8601 accepted) and filter against the layer's `esriFieldTypeDate` fields — a feature matches when any date value falls inside. Layers without date fields ignore `time`, matching ArcGIS Server's treatment of non-time-aware layers. The `where` grammar additionally accepts `TIMESTAMP '…'` and `CURRENT_TIMESTAMP ± INTERVAL n UNIT` (SECOND/MINUTE/HOUR/DAY/WEEK, plus calendar-approximate MONTH=30d/YEAR=365d) date literals, which render back as `TIMESTAMP` literals.
- Serving status update: the FeatureServer layer and service root advertise
  truthful `supportedQueryFormats` (`'JSON'`), `supportsStatistics: true`,
  `supportsAdvancedQueries: true` and `advancedQueryCapabilities`
  (`supportsPagination`/`supportsOrderBy`/`supportsDistinct`/
  `supportsReturningQueryExtent`/`supportsStatistics`/
  `supportsHavingClause: true`; `useStandardizedQueries: false`), each proved by
  the behaviour test it names. `outStatistics` (`count/sum/min/max/avg/stddev/var`)
  with `groupByFieldsForStatistics` and `having` is served over the matched set
  (T-019). pygeoapi's connect gate still fails its
  `'geoJSON' in supportedQueryFormats` assertion — honestly, because the
  facade serves Esri JSON only.
- Serving status update: `inSR` is honoured for query geometry (T-020) — the
  input geometry is interpreted in `inSR` (including the simple comma syntax
  which carries no reference) and transformed to the layer CRS before matching.
- Serving status update: `returnExceededLimitFeatures` is accepted (the REST JS
  `queryAllFeatures` loop runs unmodified with a correct `exceededTransferLimit`)
  and `maxRecordCountFactor` multiplies the page cap (`maxRecordCount × factor`,
  default 1), so oversized `resultRecordCount` values cap honestly (T-021).
- Serving status update: `Contains`/`Within`/`Touches`/`Overlaps`/`Crosses` are
  served via the envelope + intersection verbs (boundary-exactness needs a
  boundary verb the engine does not expose); `esriSpatialRelIndexIntersects`
  stays rejected with a named alternative. `quantizationParameters` is honestly
  rejected, `geometryPrecision` rounds every ordinate, `maxAllowableOffset` is
  accepted (full precision returned) (T-023).
- Consume status update: the ArcGIS REST provider sends
  `orderByFields=<objectIdField>` on every paged query for a stable paging
  sequence; the ImageServer operation surface is pinned by reconnaissance tests
  (T-027).
- T-015 closeout: the full REST JS `queryAllFeatures` loop (read
  `maxRecordCount` off the layer, page `resultOffset`/`resultRecordCount`
  with `returnExceededLimitFeatures=true`, stop on
  `returnedCount < pageSize || !exceededTransferLimit`) is replayed against
  the 34k world-cities layer and terminates with the exact `returnCountOnly`
  total in stable `OBJECTID` order with no duplicates — the stopping rule
  the real client branches on is pinned.
- Serving status update (T-040, ADR-0058): MapServer `export` honours
  `time` (the query grammar, filtered in-renderer against each layer's
  date fields so every store behaves the same), `timeRelation`
  (`esriTimeRelationOverlaps`/`Contains`/`Within`, equivalent for instant
  date values), `layerTimeOptions` (per-layer `useTime` opt-out and
  `timeDataCumulative`; non-zero `timeOffset` is honestly rejected),
  `dynamicLayers` (`mapLayer` rebind plus a `drawingInfo` override over
  the projected renderer subset; new data sources, picture/text symbols,
  label overrides and fades are honestly rejected) and `layerOption`
  (`all`/`visible`/`top`, equivalent over the flat always-visible layer
  model). The root advertises `supportsTimeRelation`,
  `supportsDynamicLayers`, `singleFusedMapCache`+`tileInfo` per served
  scheme (LOD rows replayed against the G1 cached root) and
  `exportTilesAllowed:false` — offline packaging is a closed non-goal with
  named rejects (ADR-0060); T-048 builds on that scope.

## 7.1. Deliberate non-goals (T-015 closeout audit)

Every catalogue item in `research/arcgis/conformance-sources.md` (T1–T15)
and every T-015 body candidate is landed above or recorded here. A non-goal
is rejected by name (never silently ignored) and named here with its reason:

- `f=html` Services Directory (T15): the facade is a JSON API surface by
  design (ADR-0035); a human-browsable directory is a separate concern.
  `f=html` (and any non-JSON `f`) is a typed `invalid.arguments` failure
  naming the supported value.
- GeoJSON/PBF query output (T2/T-017): the facade serves Esri JSON only;
  `f=geojson` is honestly rejected naming `supportedQueryFormats`, and the
  layer truthfully advertises `'JSON'`. pygeoapi's
  `'geoJSON' in supportedQueryFormats` gate therefore still fails — honestly.
- Coordinate-notation operations (T-044): `fromGeoCoordinateString` and
  `toGeoCoordinateString` span 8 conversion types each
  (MGRS/USNG/UTM/GeoRef/GARS/DMS/DDM/DD) with modes; the tree has no
  notation codec and the engine has no verb, so the facade rejects both by
  name with a typed `invalid.arguments` failure and does not advertise them.
  Half-parsing notations is explicitly out; a real codec needs its own
  package decision (new ADR) plus an engine verb.
- `returnZ`/`returnM`: the engine geometry model carries Z/M but the Esri
  codec serves 2D; Z/M output is explicitly rejected rather than silently
  dropped.
- `esriSpatialRelIndexIntersects` (T7b): names an index optimisation, not a
  predicate — rejected with `esriSpatialRelEnvelopeIntersects` as the named
  alternative.
- `quantizationParameters` responses (T8): quantized output is not served;
  honestly rejected, while `geometryPrecision` (rounds every ordinate) and
  `maxAllowableOffset` (accepted, full precision returned) are honoured.
- Full-text `text`, `sqlFormat`, `resultType`, `gdbVersion`,
  `historicMoment`, `datumTransformation`, `returnCentroid`,
  `distance`/`units`, `relationParam`, `returnTrueCurves`,
  `multipatchOption` (T9/T-024): each is rejected by name — dropping any of
  them would silently change the result set (`distance`/`units`, `text`,
  `resultType`) or promise data the engine does not version
  (`gdbVersion`, `historicMoment`). Raw SQL is never accepted: the facade
  evaluates only its closed where-grammar.
- Token 498/499 → `store.unavailable` (T12): by design — the engine taxonomy
  has no auth-error code and the provider takes a static token; the
  characterisation test pins the mapping.
- ImageServer `mosaicRule`/`renderingRule`/`bandIds` (T-015 closeout):
  honestly rejected on `exportImage` (shared export pipeline, so the Raster
  Image resource too) and on the catalog query (shared parse path) — each
  changes which pixels combine, so ignoring them would serve wrong bytes.
  Download clipping/re-encoding and raster functions remain absent.
- GP Service, Geocode Service, `queryRelatedRecords`, attachments,
  `htmlPopup`: no engine model behind them; absent, not emulated.
- Picture symbols (`esriPMS`/`esriPFS`): the engine's symbol dialect has no
  model for them; the §4.7 image resource is a typed `not.found`.
- Geometry Service `offset`, `cut`, `reshape`, `trimExtend`, `autoComplete`:
  no engine verb; rejected, not advertised (see above).

## 8. Documentation baseline

The retired `implementation-plan.md` (worker plugins, jobs, capability
envelopes) has been removed. Target `architecture/decisions/` and
`architecture/distilled/*` for all GeoServices work — ADR-0033's typed
per-route POST API with no job model.
