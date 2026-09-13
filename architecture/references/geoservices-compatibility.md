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
| `buffer` | `IGeometryOperations.Buffer` | **Partial** — planar, single distance, `quadrantSegments`; no `unit`, `bufferSR`, `outSR`, multi-`distances`, `unionResults`, geodesic behaviour |
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
geographic CRS. The engine buffer is planar in the geometry's own CRS. A
`distances=1000` + `unit=9001` request is not reproducible without
projection/units.

## 3. Feature Service (spec §9)

| GeoServices resource/op | Engine | Status |
| --- | --- | --- |
| Catalog (§3): folders + `services[{name,type}]` | `GET /api/catalogue`: spatial `DatasetSummary[]` | Different concept |
| `FeatureServer` root: `layers[]`, `tables[]` (§9.0) | — | Missing |
| Layer metadata (§9.1): fields, `geometryType`, `objectIdField`, `drawingInfo`, `templates`, `capabilities`, relationships, `timeInfo`, `hasAttachments` | `GET /api/datasets/{id}`: fields, geometry column/SRID/type, identity columns | Partial; different JSON |
| `query` (§9.1.4): `objectIds`, `where`, `geometry`+`geometryType`+`spatialRel`, `outFields`, `returnGeometry`, `outSR`, `returnIdsOnly`, `resultOffset`/`resultRecordCount`, `returnCountOnly`, `returnExtentOnly`, `returnDistinctValues`, `time` | `POST /api/features/query`: `dataset`, `bbox`, `filter` | **Partial** — bbox ≈ envelope-intersects; the facade implements `where` (closed grammar), field projection, `outSR`, paging, ids/count/extent-only and distinct values; `time` and the other 8 spatial relations remain absent |
| `queryRelatedRecords` (§9.1.5) | — | Missing (no relationship model) |
| `addFeatures` (§9.1.6) | `POST /api/features/write` | Partial — append-only through the API; the GeoServices facade now maps `addFeatures` onto `IFeatureEditStore.AddAsync` (ADR-0037) |
| `updateFeatures` (§9.1.7) | — | Implemented via `IFeatureEditStore.UpdateAsync` (ADR-0037), identity-backed layers only |
| `deleteFeatures` (§9.1.8) | — | Implemented via `IFeatureEditStore.DeleteAsync` (ADR-0037) |
| `applyEdits` (§9.1.9) | transactions (`begin`/`commit`/`rollback`) + write | Implemented at layer level; `rollbackOnFailure` maps to `ITransactionStore`; service-level `applyEdits` out of scope |
| attachments, `htmlPopup`, `image` (§9.2–9.6) | — | Missing |

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
| Map Service (§4): export, identify, find, tiles, layer query, image | `/arcgis/rest/services/{service}/MapServer` (ADR-0048) | **Implemented** as a projection of a `PublicationKind.Map` publication over the SDK render/tile contracts: root/layers/layer/query/identify/find, `export` (png/jpg/webp/tiff) and Web-Mercator tiles, with a `simple`/`uniqueValue`/`classBreaks` `drawingInfo`, `labelingInfo` and `domains` derived from the persisted MapLibre fragment (ADR-0050). The image (§4.7) resource is mounted and returns a typed `not.found`: it exists only for picture marker/fill symbols, which the engine's dialect has no model for |
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
- Serving status update: `f=pjson` is accepted as a JSON alias everywhere
  `f=json` is (GDAL ESRIJSON driver, pygeoapi metadata fetch); `f=geojson`
  on query is honestly rejected with a typed `invalid.arguments` failure
  naming `supportedQueryFormats` — GeoJSON output remains a non-goal.
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

## 8. Documentation baseline

The retired `implementation-plan.md` (worker plugins, jobs, capability
envelopes) has been removed. Target `architecture/decisions/` and
`architecture/distilled/*` for all GeoServices work — ADR-0033's typed
per-route POST API with no job model.
