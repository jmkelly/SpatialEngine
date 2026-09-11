# GeoServices REST Spec v1.0 ↔ SpatialEngine compatibility review

> **Status:** baseline review completed 2026-09-11. The follow-on
> decisions and delivery plan live in
> `architecture/decisions/ADR-0035-geoservices-rest-boundary-adapter.md`
> (proposed) and `architecture/geoservices-implementation-plan.md`.
> This document remains the gap analysis those build on.

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
  already anticipated by `implementation-plan.md` §5 (ArcGIS REST listed as
  a provider plugin). This is the natural fit.
- **Serving** GeoServices (engine answers ArcGIS clients) — a facade
  commitment that collides with ADR-0020 and the query-security model, and
  needs a large verb expansion. Needs its own ADR.

## 1. Protocol and interchange

| Dimension | GeoServices v1.0 | SpatialEngine | Compatible |
| --- | --- | --- | --- |
| Method | GET with query params (`f=json` required; POST only for edits) | POST JSON bodies, camelCase, OpenAPI | No |
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
| `query` (§9.1.4): `objectIds`, `where` (arbitrary SQL), `geometry`+`geometryType`+`spatialRel`, `outFields`, `returnGeometry`, `outSR`, `returnIdsOnly`, `time` | `POST /api/features/query`: `dataset`, `bbox`, `filter` | **Partial** — bbox ≈ envelope-intersects; no `where`, field projection, outSR, ids-only, time, or the other 8 spatial relations |
| `queryRelatedRecords` (§9.1.5) | — | Missing (no relationship model) |
| `addFeatures` (§9.1.6) | `POST /api/features/write` | Partial — append-only through the API; the GeoServices facade now maps `addFeatures` onto `IFeatureEditStore.AddAsync` (ADR-0037) |
| `updateFeatures` (§9.1.7) | — | Implemented via `IFeatureEditStore.UpdateAsync` (ADR-0037), identity-backed layers only |
| `deleteFeatures` (§9.1.8) | — | Implemented via `IFeatureEditStore.DeleteAsync` (ADR-0037) |
| `applyEdits` (§9.1.9) | transactions (`begin`/`commit`/`rollback`) + write | Implemented at layer level; `rollbackOnFailure` maps to `ITransactionStore`; service-level `applyEdits` out of scope |
| attachments, `htmlPopup`, `image` (§9.2–9.6) | — | Missing |

Field types: GeoServices `esriFieldType*` (OID, string, integer, double,
date-as-epoch-ms, geometry) vs engine `AttributeKind`
(`Boolean/Int64/Double/String/Geometry/DateTimeOffset/Guid`,
`core.md`). Values map; the engine model is a superset but the wire names
and date encoding differ.

## 4. Other service types

| Service | Spec | Engine |
| --- | --- | --- |
| Map Service (§4): export, identify, find, tiles, layer query, image | — | Out of scope by design — headless, no rendering/tiling (`core.md`, principles 1–2). The workbench already **consumes** Esri basemap tiles (`MapScreen.tsx`: `.../MapServer/tile/{z}/{y}/{x}`), a consumed-GeoServices precedent |
| Geocode Service (§5) | — | Absent |
| GP Service (§6): tasks, `submitJob`, job polling, results | — | Absent; no job model (ADR-0033 removed jobs) |
| Image Service (§8): export, raster functions, download | — | Explicitly out of scope (`implementation-plan.md` §4.2: raster analytics) |
| Geometry objects (§10) | point/polyline/polygon/envelope, Z/M absent | Superset — engine also has multipoint, multi-\*, geometry collection, Z/M |
| Symbol/renderer/label/domain objects (§12–15) | Map-render oriented | Absent (client-side MapLibre concern) |

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

## 8. Note on repo doc drift (unrelated but relevant)

`implementation-plan.md` Phases 9–10 still describe the superseded
capability/job/worker API (`/api/capabilities`, `/api/invocations`,
`/api/jobs/*`, `nts@2`), while `AGENTS.md`, `architecture/distilled/*` and
the host source describe ADR-0033 (typed per-route POST API, no job
model). Any GeoServices planning should target the distilled/ADR-0033
surface, not the stale plan phases.
