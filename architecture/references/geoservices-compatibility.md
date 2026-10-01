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
`src/Spatial.Host/Api`, `src/Spatial.Contracts/Http`,
`IGeometryOperations`. Review date: 2026-09-11; editing addendum: 2026-09-13.

## Verdict

**Structurally compatible at the value-model level; wire- and
capability-incompatible today.** The spec is a REST *serving* contract
(GET + `f=json`, Esri JSON geometries/features, WKID spatial references);
the engine is a typed in-process *engine* exposed over a small JSON POST
API carrying canonical binary (`SGEOM`/`SFBAT`). Nothing here is
unbridgeable. The Geometry Service surface is no longer the 3-of-19 this
verdict recorded at the 2026-09-11 baseline: today **14 of 19 Geometry
Service operations map** onto an engine verb and the other five (`offset`,
`trimExtend`, `autoComplete`, `cut`, `reshape`) are refused by name with a
typed `invalid.arguments` — §2's table is the current per-operation answer,
and a served operation there is *partial*, not exact, because its parameter
shapes differ from the spec's. The Feature Service surface is not the 1-of-6
this verdict recorded at the 2026-09-11 baseline, when the only analogue was
an append-only `addFeatures` of a different shape. Today **6 of 6 Feature
Service operations** are served — the six the layer resource numbers,
`query`, `queryRelatedRecords`, `addFeatures`, `updateFeatures`,
`deleteFeatures` and `applyEdits` (ADR-0037, ADR-0061, ADR-0077) — and §3
is the table to read for each of them, where a served operation is
*partial*, not exact, because its parameter shapes differ from the spec's.

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

19 operations. The engine verb inventory behind them is
`architecture/distilled/contracts.md` — `IGeometryOperations` (Buffer,
Intersection, Validate, Simplify, Generalize), `IGeometryMeasures` (Area,
Length, Distance, LabelPoint, Centroid), `IGeometryProcessing` (Union,
Difference, ConvexHull, Densify, Repair), `IGeometryRelations` (Relate),
`ICoordinateTransforms` (Transform, FindTransformations) and
`IGeodesicBuffering` (Buffer); four verbs was never the surface, and the
adapter reaches for the rest. The dispatcher serves **14 of the 19**, maps
each onto a named engine verb, and refuses the remaining five by name with
`invalid.arguments` (recorded non-goals, ADR-0035). Beyond the spec's 19 the
dispatcher also serves the 10.x `findTransformations` (ADR-0087) and carries
`fromGeoCoordinateString`/`toGeoCoordinateString` as explicit rejects — no
coordinate-notation codec — which is why the advertised `capabilities` string
lists 15 names and not the 18 dispatch entries. The table below is read against
that dispatch table by `GeometryCompatibilityReferenceTests`, so a row may name
an operation the dispatcher does not serve only by calling it Missing.

| GeoServices op | Engine | Status |
| --- | --- | --- |
| `project` | `ICoordinateTransforms.Transform` | **Partial** — one geometry vs array; `inSR`/`outSR` wkid vs `source`/`target` `EPSG:` string; `datumTransformation` is accepted only when it names the ranked operation project applies (ADR-0087) |
| `simplify` (generalization; §7.0.5) | `IGeometryOperations.Simplify` | **Present** — tolerance from `deviation`, or mutually exclusively from `value`; one of the two is required. It is generalization, not repair (see below) |
| `buffer` | `IGeometryOperations.Buffer` + `ICoordinateTransforms` + `IGeodesicBuffering` + `IGeometryProcessing.Union` | **Partial** — a linear `unit` against a geographic buffer CRS is a ground distance and is served by `IGeodesicBuffering` (reproject-and-buffer, 0.05% relative tolerance for a working radius up to 300 km, ADR-0075), so `distances=1000&unit=9001` against a 4326 geometry needs no `bufferSR`; `geodesic` is served on that path and refused by name elsewhere; `unionResults=true` dissolves the per-input results and is refused when the inputs resolve to different references; planar `unit` (curated table), `bufferSR`/`outSR`/`inSR` chaining, multi-`distances` and `quadrantSegments` are unchanged; vertices follow NTS quadrant segmentation, not Esri's |
| `areasAndLengths` | `IGeometryMeasures.Area` + `IGeometryMeasures.Length` | **Partial** — the docs name the array `polygons` (older docs/samples `polys`); both aliases and `geometries` are accepted for the same value. `{areas, lengths}` in the units of `sr`. Planar only: `calculationType` absent or `planar` answers, and `geodesic`/`preserveShape` are refused by name (no geodesic measure verb) |
| `lengths` | `IGeometryMeasures.Length` | **Partial** — as `areasAndLengths`, with the docs' `polylines` alias and the same planar-only `calculationType` |
| `relation` (DE-9IM `relationParam`) | `IGeometryRelations.Relate` | **Partial** — a `relation` naming `esriSpatialRelIntersects`/`Disjoint`/`Contains`/`Within`/`Touches`/`Overlaps`/`Crosses`/`Equals`, a bare DE-9IM pattern, or `esriSpatialRelRelation` with a `relationParam` pattern (including the `RELATE(G1, G2, 'pattern')` spelling) is served; the dimension-dependent `esriSpatialRelTouches`/`Overlaps`/`Crosses` are served too, and `esriSpatialRelIntersects` is answered by the OGC intersect pattern union (`T********`/`*T*******`/`***T*****`/`****T****`) rather than by building the intersection (SpatialEngine-51k) — all out of the same pattern table the query path reads (ADR-0036), read as the OGC table verbatim with the feature geometry as the matrix's left operand (ADR-0106) — with `Contains` and `Within` read in the protocol's frame, the feature's relation to the input geometry (ADR-0171) — with the left geometry in the feature's role and the right in the query's, so the two endpoints answer one way. `Contains` and `Within` read that table too, so no DE-9IM pattern is restated on this path (SpatialEngine-dih); `Overlaps` and `Crosses` are dimension-dependent here exactly as on the query path — the pattern is keyed on the pair's dimensions, with the left geometry in the feature's role. Inputs are the docs-verbatim `geometries1`/`geometries2` with `sr1`/`sr2` (one shared `sr` also accepted; differing `sr1`/`sr2` are refused) and the legacy `geometries`/`geometry` pair. One flag per `geometries1` geometry, 1 when any `geometries2` geometry relates; an unrecognised name is rejected by name. The cells the matrix reports are the provider's and are what DE-9IM means: `Relate(a,b)` and `Relate(a,b,pattern)` are one computation, a point operand contributes no boundary (a point on a line's endpoint meets it in the line's boundary row and the pair is not disjoint), and a vertex-only contact is dimension 0 in position 5 rather than the dimension 1 of an edge-sharing contact (ADR-0166). Where the served table and the reference implementation's own named predicates disagree it is on `Crosses` alone, in two measured classes: a line lying wholly inside a polygon and touching its boundary reads `true` here and `false` there — the served alternation asks that the interiors meet and that the lower-dimensional interior reach the higher-dimensional boundary, and says nothing about the line leaving the area, which the reference implementation's predicate additionally requires; that class reads `true` only in the frame where the surface is the left geometry, and `false` when the line is, so the same pair asked the other way round is not `Crosses` (ADR-0106, ADR-0166, ADR-0169) — and a pair with a `MultiPoint` on one side reads `false` here because no served mask is named for it (ADR-0106) and `true` there. Every pair either way is named in the engine's characterisation suite |
| `labelPoints` | `IGeometryMeasures.LabelPoint` | **Partial** — one interior point per input; the array is the docs' `polygons` (`polys`) alias or `geometries`. The point is guaranteed interior, which agrees with Esri's centre for convex shapes; for a concave shape Esri's representative point may differ (out of scope, fixture delta) |
| `distance` | `IGeometryMeasures.Distance` | **Partial** — the docs' singular `geometry1`/`geometry2` (not the `geometries1`/`geometries2` of `relation`), answered as `{distance}`: the planar minimum distance in the units of `sr`. No `unit` conversion and no geodesic measure |
| `densify` | `IGeometryProcessing.Densify` | **Partial** — `maxSegmentLength` is required and is in the units of `sr`; every segment longer than it is subdivided. The array is `geometries`, one result per input |
| `generalize` (Douglas-Peucker; §7.0.13) | `IGeometryOperations.Generalize` | **Present** — the deviation allowance from `maxDeviation` (ADR-0079), which returns input vertices only and spends the allowance conservatively; `Simplify` serves the same generalization under `simplify`'s own parameter names |
| `convexHull` | `IGeometryProcessing.ConvexHull` | **Partial** — the hull of the whole `geometries` array as a single result geometry; ring orientation and vertex order follow NTS, not Esri (fixture delta) |
| `offset` | — | **Missing** — no engine verb; the dispatcher rejects it by name (`invalid.arguments`, recorded non-goal, ADR-0035) |
| `trimExtend` | — | **Missing** — as `offset` |
| `autoComplete` | — | **Missing** — as `offset` |
| `cut` | — | **Missing** — as `offset` |
| `difference` | `IGeometryProcessing.Difference` | **Partial** — `geometries` (an array) minus one `geometry`, the mirror image of `intersect`'s argument shapes; engine disjoint→empty is spec-compatible |
| `intersect` | `IGeometryOperations.Intersection` | **Partial** — `(array, geometry)` vs `(left, right)`; engine disjoint→empty (spec-compatible) |
| `reshape` | — | **Missing** — as `offset` |
| `union` | `IGeometryProcessing.Union` | **Partial** — a single CRS for the inputs, as the operation assumes, dissolved into one result geometry; ring orientation and vertex order follow NTS, not Esri (fixture delta) |

**Generalization:** both `generalize` (§7.0.13) and `simplify` (§7.0.5)
are Douglas-Peucker generalization, so both map to the engine's `Simplify`
(`contracts.md`), under their own parameter names: `maxDeviation` for
`generalize`, `deviation` (or, mutually exclusively, `value`) for
`simplify`. Neither accepts a request without a tolerance, and neither
touches a self-intersecting ring other than by thinning it.

**Repair:** topological repair is `IGeometryProcessing.Repair` (NTS
`GeometryFixer`, ADR-0036) and no Esri Geometry Service operation names
it. A captured `simplify` request that omits the tolerance came back from
Esri repaired into two rings (`tests/fixtures/esri-docs/geometryserver/
simplify-bowtie.json`); the engine does not reproduce that, because the
operation whose name says generalization does not repair. The fixture
replays the recorded request and pins the honest `invalid.arguments`
reject instead.

**Buffer semantics trap:** GeoServices applies a `unit`, can buffer in a
third CRS (`bufferSR`) and geodesically for points/multipoints in a
geographic CRS. The engine has both paths. A projected `bufferSR` reproduces
the projected result exactly, and remains the answer when the extent is too
large for a local plane. A linear `unit` against a geographic buffer CRS is
a ground distance: it goes to `IGeodesicBuffering`, which reprojects onto a
transverse Mercator working plane centred on the work and buffers there
(ADR-0075). That is within **0.05% relative of the geodesic** for a working
radius (input envelope half-diagonal plus the distance) up to **300 km**;
past that the engine refuses with the radius, the limit and the remedy
instead of answering with a shape it cannot stand behind. An angular `unit`
still buffers planar degrees, and `geodesic=true` is refused there (the
engine's ground-distance verb works in metres). `unionResults=true`
dissolves the per-input buffers into one geometry.

## 3. Feature Service (spec §9)

| GeoServices resource/op | Engine | Status |
| --- | --- | --- |
| Catalog (§3): folders + `services[{name,type}]` | `GET /api/catalogue`: spatial `DatasetSummary[]` | Different concept |
| `FeatureServer` root: `layers[]`, `tables[]` (§9.0) | — | **Served** — spatial datasets under `layers`, datasets without a geometry field under `tables` (stable ids from the single id space; table metadata reports `type: Table` and supports the safe query subset) |
| Layer metadata (§9.1): fields, `geometryType`, `objectIdField`, `drawingInfo`, `templates`, `capabilities`, relationships, `timeInfo`, `hasAttachments` | `GET /api/datasets/{id}`: fields, geometry column/SRID/type, identity columns | Partial; different JSON |
| `query` (§9.1.4): `objectIds`, `where`, `geometry`+`geometryType`+`spatialRel`, `outFields`, `returnGeometry`, `outSR`, `returnIdsOnly`, `resultOffset`/`resultRecordCount`, `returnCountOnly`, `returnExtentOnly`, `returnDistinctValues`, `time` | `POST /api/features/query`: `dataset`, `bbox`, `filter` | **Partial** — bbox ≈ envelope-intersects; the facade implements `where` (closed grammar, including `TIMESTAMP`/`CURRENT_TIMESTAMP ± INTERVAL` date literals), field projection, `outSR`, paging, ids/count/extent-only and distinct values, and `time` (instant or start,end extent with `null` infinity bounds, filtered against the layer's date fields and ignored when the layer has none); `EnvelopeIntersects`/`Intersects`/`Contains`/`Within`/`Touches`/`Overlaps`/`Crosses` are served — all but the first as exact DE-9IM patterns over `IGeometryRelations.Relate` (ADR-0036), read as the OGC table verbatim with the feature geometry as the matrix's left operand, and with `Contains`/`Within` named in the protocol's frame — the relation of the feature to the input geometry, so the served `Contains` is the feature contained *in* the query geometry (ADR-0171) — `Within` its transpose, `Crosses` keyed on the dimension pair, and a point never `Crosses` an area (ADR-0106) — envelope-prefiltered, with `Intersects` answered by the OGC intersect pattern union rather than by building the intersection geometry (SpatialEngine-51k), and only `esriSpatialRelIndexIntersects` stays rejected (it names an index optimisation, not a predicate). The service-level `FeatureServer/query` (S1) fans the same subset across layers and tables with `layerDefs` (all three syntaxes) and returns one feature set, count, or id list per layer (ADR-0061); layer-only shapes (extent, distinct, statistics, unique ids) name the layer route instead |
| `generateRenderer` (S4, feature-service layer) | — | **Served** — the single T-039 classifier (`MapGenerateRenderer`, ADR-0055) reused on the FeatureServer surface; byte-identical renderers on both surfaces (ADR-0061) |
| `validateSQL` (S4, feature-service layer) | — | **Served** — server-side WHERE validation returning the S4 `isValidSQL` shape with 3001/3002/3008 codes; `expression`/`statement` validate as not-supported, never run (ADR-0061) |
| `queryBins` / `queryTopFeatures` / `queryAnalytic` (S4) | — | Honestly rejected — mounted typed `invalid.arguments` naming the served alternative (`outStatistics`+`groupByFieldsForStatistics`; `orderByFields`+`resultRecordCount`); unadvertised (ADR-0061) |
| `queryRelatedRecords` (§9.1.5) | — | **Served over declared relationships** (ADR-0077): a map layer declares a relationship to another of its layers over two key columns (one-to-one, one-to-many, or many-to-many through a join dataset); the declaring layer advertises it in its `relationships` metadata (`id`/`name` is the declared name, `relatedLayerId`, `title`, `esriRelationshipType*`). `queryRelatedRecords` traverses the declaration as the related layer's own query — its `where`, `outFields`, `geometry`/`spatialRel`, `time` and `outSR` all apply — and answers `{fields, relationships:[{name, relatedId, fields}]}`, one group per origin record that has related records. `relate`/`unrelate` move the same key behind the edit verbs' admin gate, one result per origin/related pair. Declarations are validated structurally when the map is stored and against the live schemas at the declaration boundary; a layer that declares nothing omits the `relationships` key |
| `addFeatures` (§9.1.6) | `POST /api/features/write` | Partial — append-only through the API; the GeoServices facade now maps `addFeatures` onto `IFeatureEditStore.AddAsync` (ADR-0037) |
| `updateFeatures` (§9.1.7) | — | Implemented via `IFeatureEditStore.UpdateAsync` (ADR-0037), identity-backed layers only |
| `deleteFeatures` (§9.1.8) | — | Implemented via `IFeatureEditStore.DeleteAsync` (ADR-0037) |
| `applyEdits` (§9.1.9) | transactions (`begin`/`commit`/`rollback`) + write | Implemented at layer level; `rollbackOnFailure` maps to `ITransactionStore`; service-level `applyEdits` out of scope |
| attachments (§9.2–9.6) | — | **Served on the `IFeatureAttachmentStore` capability** (ADR-0065/ADR-0066): layers whose store exposes the capability advertise `hasAttachments` with `attachmentProperties` (id, name, size, contentType, keywords); `queryAttachments` returns per-feature `attachmentGroups`, the per-feature `attachments` resource its `attachmentInfos`, and a per-attachment resource serves the bytes; `addAttachment`/`updateAttachment` (multipart `attachment` part) and `deleteAttachments` (per-id results) mutate behind the single admin token. Stores without the capability (demo, ArcGIS REST) keep the honest surface: `hasAttachments: false`, empty reads, typed `invalid.arguments` writes; the memory, SQL Server and PostGIS providers all expose it (ADR-0065 §2, ADR-0073) |
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
| Map Service (§4): export, identify, find, tiles, layer query, image | `/arcgis/rest/services/{service}/MapServer` (ADR-0048) | **Implemented** as a projection of a `PublicationKind.Map` publication over the SDK render/tile contracts: root/layers/layer/query/identify/find, `export` (png/jpg/webp/tiff) and Web-Mercator tiles, with a `simple`/`uniqueValue`/`classBreaks` `drawingInfo`, `labelingInfo` and `domains` derived from the persisted MapLibre fragment (ADR-0050). `legend`, `queryDomains` and `queryLegends` project the same persisted style (ADR-0055), and each layer serves `generateRenderer` (equal-interval `classBreaksDef`, single-field `uniqueValueDef`). `export` honours `time`/`timeRelation`/`layerTimeOptions` (per-layer opt-out, cumulative display; non-zero offsets rejected; `timeRelation` serves `Overlaps`/`Contains`/`Within` over a layer's designated feature temporal extent and refuses the latter two by name on a layer that designates none), `dynamicLayers` (mapLayer rebind plus a `drawingInfo` override over the projected renderer subset) and `layerOption` (`all`/`visible`/`top`), and the root advertises `supportsTimeRelation` as whether a served layer designates its start/end date fields (ADR-0100, ADR-0182), `supportsDynamicLayers`, `singleFusedMapCache`+`tileInfo` per served scheme and `exportTilesAllowed:false` (ADR-0058). The offline/async surface — `exportTiles`+`estimateExportTileSize` (map + image variants), the WMTS triple, `generateKml`/the `kml` image, async `jobs` — is mounted and rejected by name with typed `invalid.arguments` pointing at the live alternative (ADR-0060; no packaging or job model). Vector tiles (MVT, `VectorTileServer`) and OGC API Tiles are live surfaces (ADR-0070, superseding ADR-0062): the neutral and Esri MVT routes plus map-scoped OGC landing, collections, TileJSON and negotiated tile data share the T-002 scheme/cache contracts. Offline `.vtpk` packaging and `exportTiles` remain rejected/non-goal (ADR-0033/ADR-0060). The image (§4.7) resource is mounted and returns a typed `not.found`: it exists only for picture marker/fill symbols, which the engine's dialect has no model for |
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
   EPSG is 3857). The catalogue is a set of vendored EPSG definitions — WKT
   text the provider reads, not WKT a client can send — plus the generated
   UTM families, and there is no WKT input. A facade needs a WKID↔EPSG map
   (and a WKT decision); the catalogue's own WKT is the vocabulary such a
   map would be written in.

The plan already positions ArcGIS REST as a **provider** (§5 architecture
diagram), i.e. the consume direction is the one the architecture
anticipated.

## 6. Concrete work items, if serving is pursued

Ordered by dependency:

1. Esri geometry JSON codec (x/y, `paths`, `rings`, `points`, `xmin..`,
   plus the simple comma syntax and `{"url": ...}` form — the latter is an
   SSRF concern and should be rejected).
2. WKID↔EPSG mapping; decide whether to accept `{wkt}`.
3. Additional verbs: `generalize` and `simplify` (both `Simplify`, each
   with its own tolerance parameter), `union`/`difference`, area/length,
   distance, densify, convex hull, offset — **delivered**; topological
   repair is `IGeometryProcessing.Repair` with no Esri operation name, so
   it is reachable through the engine API and not through this facade.
   The `spatialRel` predicates are delivered too,
   as exact DE-9IM intersection patterns over `IGeometryRelations.Relate`
   (ADR-0036) behind the query path's envelope pre-filter, read as the OGC
   table verbatim with the feature geometry as the matrix's left operand
   (ADR-0106), and in the protocol's frame for the two antisymmetric verbs:
   `spatialRel` names the feature's relation to the input geometry
   (ADR-0171).
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
  `Spatial.Stores.*` over the existing `IDataCatalogue`/`IFeatureStore`
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
- Serving status update (ADR-0075, supersedes the T-044 note below):
  `buffer` serves a **linear `unit` against a geographic buffer CRS** through
  `IGeodesicBuffering` — reproject onto a local transverse Mercator, planar
  buffer, project back — so the commonest request (`distances=1000&unit=9001`
  against a 4326 geometry) no longer needs a projected `bufferSR`. Tolerance:
  0.05% relative for a working radius up to 300 km; past that a typed
  `invalid.arguments` failure naming the radius and the remedy.
  `geodesic=true` is served on that path and refused by name against an
  angular unit, no unit or a projected `bufferSR`. `unionResults=true`
  dissolves the per-input buffers into one geometry. The planar path
  (projected `bufferSR`, angular `unit`, no `unit`) is unchanged and remains
  the exact answer inside a valid zone.
- Serving status update (T-044): `buffer` honours `unit` (curated linear +
  angular code table, factors verified against the hosted service) with
  `bufferSR`/`outSR`/`inSR` chaining per spec §7.0.6 via
  transform-then-buffer; `geodesic=false` is accepted as planar while
  `geodesic=true`/`unionResults` stay rejected. `findTransformations`
  is a ranked search over the provider's transformation graph (ADR-0087):
  same datum → `[]`, a datum step → the direct composed Helmert (the path the
  engine applies), the concatenated path through the WGS 84 pivot and the
  three-parameter reduction, each carrying its steps, the seven Helmert
  parameters it applies, its area of use and a derived accuracy (ADR-0086
  says where the datum accuracy and extent come from). It is symmetric (a
  reversed request returns the same operations with
  `transformForward: false`), `extentOfInterest` filters rather than refusing
  (in the source CRS's own coordinates, reprojected to the geographic boxes
  the catalogue records), `vertical=false` is accepted while `vertical=true`
  stays refused, and `numOfResults`/`numTransformations` slice the ranked
  list — every candidate by default. `project` accepts a `datumTransformation`
  that names the operation it applies and refuses any other by naming it.
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
- Serving status update (SpatialEngine-u2x.16, ADR-0085): the three
  "no engine verb" query rejects are served. `distance`/`units` is a band
  measured from the query geometry and applied as a buffer **in the layer
  CRS** — the Geometry Service's transform-then-buffer rule, with the unit
  code resolved from the same curated table by the same projected/geographic
  rule — so it composes with every exact `spatialRel`; a `distance` without a
  `geometry`, or a `units` without a `distance`, is a named failure.
  `returnCentroid` is a new `IGeometryMeasures.Centroid` verb (area centroid
  for polygons, not the envelope middle) written beside each feature's
  geometry, and rejected where there is no feature to attach it to.
  `returnZ`/`returnM` select the output ordinates and the writer now states
  the `hasZ`/`hasM` flags the Esri coordinate arrays need. The layer resource
  advertises `hasZ`/`hasM` too, and only for the ordinates the store declares
  (ADR-0084, recorded in §7.1).
- Serving status update (SpatialEngine-m3q): the unit parameter takes either
  spelling a client sends — the numeric `esriSRUnitType` code (`units=9001`)
  or the `esriSRUnit_*` symbolic name (`units=esriSRUnit_Meter`, the form the
  REST JS allowlist research records in T9). The name resolves to the same
  curated code, so the query `units` and the Geometry Service `unit` are
  unchanged for a numeric client and stop being a typed `invalid.arguments`
  for a symbolic one. Only the names of the curated codes are mapped; an
  unknown name is a named failure whose message lists both spellings.
- Serving status update: the layer resource advertises
  `supportsQuantization` (top level, where the ArcGIS REST JS gate reads it,
  and inside `advancedQueryCapabilities`) and
  `advancedQueryCapabilities.supportsPaginationOnAggregatedQueries`, both
  proved by the behaviour that earns them — quantized ordinates (ADR-0079)
  and an `outStatistics` response that pages with
  `exceededTransferLimit`/`resultPaginationToken`. A flag the facade does not
  earn is omitted rather than emitted as `false` (ADR-0081). A table (a
  dataset with no geometry field) omits the per-layer quantization flag.
- Serving status update: `inSR` is honoured for query geometry (T-020) — the
  input geometry is interpreted in `inSR` (including the simple comma syntax
  which carries no reference) and transformed to the layer CRS before matching.
- Serving status update: `returnExceededLimitFeatures` is accepted (the REST JS
  `queryAllFeatures` loop runs unmodified with a correct `exceededTransferLimit`)
  and `maxRecordCountFactor` multiplies the page cap (`maxRecordCount × factor`,
  default 1), so oversized `resultRecordCount` values cap honestly (T-021).
- Serving status update: `Contains`/`Within`/`Touches`/`Overlaps`/`Crosses` are
  served exactly, as DE-9IM intersection patterns over the engine's own
  relation verb (`IGeometryRelations.Relate`, ADR-0036) with the envelope
  tests kept as the cheap pre-filter; a containee lying *on* the container's
  boundary is no longer read as `Contains`, a point or line on the boundary
  is now `Touches`, and a line crossing a feature is no longer `Touches`
  (the envelope approximation called a crossing line's intersection
  "degenerate" and rejected it only as a containment).
- Serving status update: `Overlaps` and `Crosses` are keyed on the pair's
  *dimension pair*, not on which side is higher (SpatialEngine-u2x.56):
  `Overlaps` is `T*T***T**` (A/A) and `1*T***T**` (L/L) — the line/line
  interiors have to meet in dimension one — and `Crosses` is `T**T*****`
  (A/L), `T*T******` (L/A) and `0********` (L/L) — meeting in dimension
  zero. One pattern across both same-dimension cases read two crossing lines
  (`0F1FF0102`) as an overlap, and gating equal dimensions out of `Crosses`
  meant that same pair never crossed. A pair the reference does not relate
  at those dimensions reads false. `Contains`, `Within`, `Touches` and
  `Intersects` are unchanged.
- Serving status update: the *reading* of that table is settled (ADR-0106):
  the OGC Simple Features patterns served verbatim, the feature geometry as
  the matrix's left operand, and the pattern keyed on the pair's dimension
  pair rather than on a dimension mask. So:
  - `Contains` is `T*F**F***` and `Within` is `T*****FF*` — each the
    other's transpose, so the pair is one predicate asked both ways, in the
    frame the protocol asks for (ADR-0171: `spatialRel` names the feature's
    relation to the input geometry, so `Within` is "the feature contains the
    query geometry"). A feature straddling the query geometry's edge contains
    neither it nor anything else (its interior reaches the query's exterior);
    a feature sharing part of the query's *boundary* is `Within` it, because
    the mask excludes the feature's boundary against the query's exterior and
    not its interior.
  - `Crosses` is the dimension-pair triple above. **A point is never
    `Crosses` an area** — that dimension pair names no pattern, so none is
    asked; a point inside an area is `Contained` in it by the served
    `Contains` and a point on its boundary is `Touches`. The reading that was
    discarded asked `0********` there and answered true, which is the case its
    own dimension switch existed to take back.
  - The frame itself is settled by ADR-0171, measured against a live
    FeatureServer rather than argued: see §7.1 for the observation and the
    direction it settles. The other four verbs are closed under
    transposition, so it moved neither.
- Serving status update: the DE-9IM pattern grammar is one type
  (`Spatial.Core.Geometry.De9imPattern`) read on both sides of the relation
  call, and the boundary no longer recognises only part of it: `relation`
  used to tell a pattern from a relation *name* by whether it was spelled
  from `T`, `F`, `*` and `0`, so a pattern naming a dimension — `1*T***T**`,
  the line/line overlap pattern — was rejected by name as an unsupported
  relation, and a `relationParam` reaching the engine with an unknown cell
  was answered rather than rejected (SpatialEngine-imj, ADR-0036). A pattern
  is nine cells; a wrong length or a cell outside `T`/`F`/`0`/`1`/`2`/`*` is
  `invalid.arguments` naming the grammar.
- Serving status update: `Touches` is the three OGC touches masks as one
  dimension-free union (`FT*******`/`F**T*****`/`F***T****`,
  SpatialEngine-u2x.35), so a point or line on the other's boundary is
  `Touches` in *either* operand order — the dimension-keyed pick that
  replaced them missed position 2, which is where the contact lands for a
  point or line on the left, and a point or line on a query polygon's
  boundary read as not touching. `Intersects` stays the
  non-empty intersection and `esriSpatialRelEnvelopeIntersects` stays the
  envelope test. `esriSpatialRelIndexIntersects` stays rejected with a named
  alternative. `quantizationParameters` now snaps every ordinate (x, y, z
  and m) to the view grid and spends the rest of the budget on the same
  generalization verb, `geometryPrecision` rounds every ordinate, and
  `maxAllowableOffset` is honoured as a deviation allowance rather than
  accepted and ignored (T-023, T8, ADR-0075).
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
- Serving status update (T-040, ADR-0058, amended by ADR-0100): MapServer
  `export` honours
  `time` (the query grammar, filtered in-renderer against each layer's
  date fields so every store behaves the same), `timeRelation`
  (all three relations are read and applied over a layer's designated feature
  temporal extent, so `Contains` and `Within` answer as themselves; a layer
  designating none has no extent to compare a window against and refuses them
  by name rather than serving them as overlaps — ADR-0100, ADR-0175,
  ADR-0182), `layerTimeOptions` (per-layer `useTime` opt-out and
  `timeDataCumulative`; non-zero `timeOffset` is honestly rejected),
  `dynamicLayers` (`mapLayer` rebind plus a `drawingInfo` override over
  the projected renderer subset; new data sources, picture/text symbols,
  label overrides and fades are honestly rejected) and `layerOption`
  (`all`/`visible`/`top`, equivalent over the flat always-visible layer
  model). The root advertises `supportsTimeRelation` as whether one of its
  served layers designates its start/end date fields,
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
- `returnZ`/`returnM` (SpatialEngine-u2x.16, ADR-0085): **served**. Checked
  rather than assumed: `Spatial.Core`'s canonical binary codec round-trips
  `Xyzm` exactly, and the Esri codec reads and writes Z/M ordinates. The loss
  was codec depth, not the engine — the writer emitted a three-ordinate Esri
  coordinate array without the `hasZ`/`hasM` flag that says whether the third
  number is a Z or an M (the codec's own reader uses it to decide), and a
  true flag was rejected outright. Both are fixed: the writer states the
  flags for whatever it writes, and the flags select the output ordinates.
- `hasZ`/`hasM` on the layer resource: **served**, as a description of the
  data rather than a capability (ADR-0084). The engine now knows what a
  dataset's geometry column carries: `DatasetDescription.GeometryLayout`
  carries the layout the store *declares*, PostGIS reads it from the column's
  typed modifier (`geometry(PointZ,4326)` → `xyz`, `geometry(PointZM,4326)` →
  `xyzm`), and the layer advertises `hasZ`/`hasM` only for the ordinates it can
  prove. A two-dimensional layer, and a geometry column declared plain
  `geometry` (which constrains nothing and so proves nothing), advertise
  neither key rather than `false` — ArcGIS clients send Z in query geometry and
  edit payloads once `hasZ` is true, so over-advertising is a broken round
  trip while under-advertising is only a client that asks for less. The ArcGIS
  REST store proves the same thing from the remote layer's own `hasZ`/`hasM`
  declaration (ADR-0091) and asks the remote for exactly the ordinates it
  advertises. The **read** side needed the same declaration and did not have
  it: the recorded corpus (`tests/fixtures/arcgis/captured/`) has 18 layers
  declaring `hasZ: true` and **no flagged response geometry among them** — a
  remote states its layout once, on the layer resource. So a response geometry
  that flags nothing is read by the layout its dataset declares, and an Esri
  point's `z`/`m` property is read as the ordinate it names, flag or no flag
  (ADR-0142). Without that, a hasZ layer's elevation was asked for by
  ADR-0091 and then dropped on the way in. The **MapServer** layer record carries the same keys under the
  same rule (ADR-0125), off the same `DatasetDescription` — a MapServer layer
  resource is the FeatureServer document with a `drawingInfo` attached, and a
  3D dataset described as 2D by the map surface while the FeatureServer
  describes it as 3D is the same inconsistency in the opposite direction.
- `esriSpatialRelIndexIntersects` (T7b): names an index optimisation, not a
  predicate — rejected with `esriSpatialRelEnvelopeIntersects` as the named
  alternative.
- The **direction** of `esriSpatialRelContains` / `esriSpatialRelWithin`
  (SpatialEngine-2ve, ADR-0171): the one reading this surface got wrong for
  two years, decided against a live ArcGIS Server rather than against the OGC
  table alone. `spatialRel` is documented as *the spatial relationship to be
  applied to the **input geometry***, so the named relation is the input
  geometry's: `Within` is "the feature contains the input geometry" and
  `Contains` is "the feature is contained in the input geometry" — the OGC
  names of the *query* geometry, not of the feature. A live FeatureServer
  agrees, and says so on both operand sizes
  (`sampleserver6.arcgisonline.com/.../USA/MapServer/3/query`, layer
  *Counties*, 2026-10-03, `returnCountOnly=true`):

  | input geometry | `esriSpatialRelContains` | `esriSpatialRelWithin` |
  | --- | --- | --- |
  | point at (-71.5, 41.6), inside one county | `{"count":0}` | `{"count":1}` |
  | ring (-71.9,41.2)-(-71.2,42.1), covering six counties | `{"count":6}` | `{"count":0}` |

  The facade now answers the same way round: served `Contains` is `T*F**F***`
  (the feature inside the query geometry, envelope-prefiltered the same way)
  and served `Within` is `T*****FF*`, each the other's transpose. The other
  four verbs did **not** move, and could not: OGC defines `Touches`,
  `Overlaps`, `Crosses` and `Intersects` as symmetric and their served masks
  are closed under transposition, so the frame cannot change a verdict of
  any of them — which is why an inverted frame survives every test that only
  asks symmetric verbs. Both operand orders of a nesting are pinned for every
  served verb (`SpatialRelDirectionTests`), so the direction is a test and not
  a reading of whichever table is in the tree. This is a **breaking change to
  the served surface**: a client that used `esriSpatialRelWithin` to find the
  features containing an input geometry now gets the features it contains,
  and one that used `esriSpatialRelContains` for the common point-in-polygon
  query starts working. The Geometry Service's `relation` operation moves
  with it, because both surfaces resolve the verb out of the one pattern
  table (ADR-0106 §4); the online GeometryServer's own `relate` returns an
  empty body for every named relation, so its direction is settled by that
  shared table rather than by observation.
- The two **edge semantics** ADR-0171 left open, settled by observation
  (SpatialEngine-msc; requests, results and dates in
  `research/arcgis/conformance-sources.md` §5, against Esri's own
  `sampleserver6.arcgisonline.com/.../USA/MapServer`, 2026-10-03). Neither
  moved the served mask; both are pinned so the choice is deliberate rather
  than inherited.
  - **`Contains` against a point input geometry** — the strict mask
    `T*****FF*` is *not* rejected for a point, and no `covers` reading is
    needed. A point contributes no boundary of its own and never reaches the
    area's exterior, so every cell the strict mask asks beyond the interior
    intersection is empty. Live, a point at (-71.5, 41.6) inside one county
    answers `Within` → 1 and `Contains` → 0: the point-in-polygon query a
    QGIS or REST JS client sends keeps working, on the mask as written.
  - **A coincident edge is a `Touches`, not a nesting.** Querying the same
    Counties layer with the *Providence* county polygon verbatim answers
    `Within` → 1 (the county itself) and `Touches` → 6 (its edge-sharing
    neighbours), so ArcGIS's contains is strict about a shared boundary and is
    not `covers`; an identical polygon is `within`, an edge-sharing neighbour
    is not. The engine's `relate` agrees cell for cell —
    `NtsRelateCellSemanticsTests` reads `FF2F11212` for a coincident-edge
    rectangle pair with both containment masks false, so a shared boundary
    buys a position-5 cell and never a position-1 one. (The bead reported
    NetTopologySuite resolving that pair as *covered*; it does not, and the
    measurement is the record.)
  - **`Crosses` for a point strictly inside an area** is `false`, in both
    operand orders. Live, an envelope holding eight city points answers
    `Contains` → 8 and `Crosses` → 0, and a polygon input area holding one
    point answers `Crosses` → 0; the same envelope against the *Highways*
    layer answers `Crosses` → 5, so the verb is served and it is the 0-D
    operand that is excluded. DE-9IM would read `T*****T**` true; ArcGIS's
    wording ("partially inside, partially outside") is what it serves and a
    point cannot satisfy it. This is the ADR-0106 reading the engine already
    served, so nothing moved — a point pair names no `Crosses` mask at all.
- `quantizationParameters`/`maxAllowableOffset` (T8): no longer a non-goal.
  Both are served through `IGeometryOperations.Generalize`, the verb that
  states a deviation allowance rather than an algorithm tolerance
  (ADR-0079): the response stays within the allowance, keeps its geometry
  kind even when the allowance is wider than the feature, and is
  byte-identical to full precision at an allowance of zero. An unservable
  `mode` or `originPosition` is still rejected by name.
- `sqlFormat`, `resultType`, `datumTransformation`, `relationParam`,
  `returnTrueCurves`, `multipatchOption` (T9/T-024): each is rejected by
  name. Raw SQL is never accepted: the facade evaluates only its closed
  where-grammar, so `sqlFormat` names the one thing the facade will never do;
  `relationParam` would mean composing DE-9IM patterns out of client text.
- `text` (T9): a **wider feature, not depth** — a full-text search needs an
  inverted index and a tokenizer per field, which is a store and catalogue
  decision (a new verb surface plus a per-store implementation), not a
  projection one. Rejected by name; `where` with LIKE is the served
  alternative.
- `gdbVersion`, `historicMoment` (T9): **wider features, not depth** — time
  travel needs versioned rows in every store, a versioned dataset contract
  and a resolution rule for a deleted feature. Rejected by name rather than
  approximated with a hidden timestamp column, which would answer a
  different question.
- `distance`/`units` and `returnCentroid`: were here as "the engine has no
  verb" and are **served** (SpatialEngine-u2x.16, ADR-0085) — see §7.
- Token 498/499 → `store.unavailable` (T12): by design — the engine taxonomy
  has no auth-error code and the provider takes a static token; the
  characterisation test pins the mapping.
- ImageServer `mosaicRule`/`renderingRule`/`bandIds` (T-015 closeout):
  honestly rejected on `exportImage` (shared export pipeline, so the Raster
  Image resource too) and on the catalog query (shared parse path) — each
  changes which pixels combine, so ignoring them would serve wrong bytes.
  Download clipping/re-encoding and raster functions remain absent.
- GP Service, Geocode Service, `htmlPopup`: no engine model behind them;
  absent, not emulated. `queryRelatedRecords` and attachments have since
  landed (ADR-0077 and ADR-0065/ADR-0066 respectively) and are served above.
- Picture symbols (`esriPMS`/`esriPFS`): the engine's symbol dialect has no
  model for them; the §4.7 image resource is a typed `not.found`.
- Geometry Service `offset`, `cut`, `reshape`, `trimExtend`, `autoComplete`:
  no engine verb; rejected, not advertised (see above).

## 8. Documentation baseline

The retired `implementation-plan.md` (worker plugins, jobs, capability
envelopes) has been removed. Target `architecture/decisions/` and
`architecture/distilled/*` for all GeoServices work — ADR-0033's typed
per-route POST API with no job model.
