# Geometry Service compatibility matrix

Sources (checked 2026-09-14):
- S1 Geometry Service: https://developers.arcgis.com/rest/services-reference/enterprise/geometry-service/
- S2 Op pages (21): `project/`, `generalize/`, `buffer/`, `intersect/`, `simplify/`,
  `areas-and-lengths/`, `lengths/`, `distance/`, `relation/`, `densify/`,
  `convex-hull/`, `difference/`, `union/`, `label-points/`, `offset/`, `cut/`,
  `reshape/`, `trim-extend/`, `auto-complete/`, `findtransformations/`,
  `from-geocoordinatestring/`, `to-geocoordinatestring/` (+ `geometry-objects/`)
- G1 Ground truth: `ground-truth/geometry-root.utility.json`
  (`utility.arcgisonline.com/.../Geometry/GeometryServer?f=json` → only
  `serviceDescription`; the operation list is advertised per-op, not at root)

Our surface: `src/Spatial.Adapter.GeoServices/GeometryService.cs` (dispatch +
14 ops), `GeoServicesEndpoints.Geometry.cs` (routes), SDK
`IGeometryOperations`/`IGeometryMeasures`/`IGeometryProcessing`/`IGeometryRelations`
(ADR-0036), `ICoordinateTransforms`/`ICrsDirectory`.

## 1. Operations (21)

| ArcGIS op | Ours | Status | Evidence |
|---|---|---|---|
| `project` | served | **Have** | `GeometryService.cs:Operations["project"]` → `ICoordinateTransforms.Transform` |
| `generalize` (Douglas-Peucker) | served | **Have** | `→ IGeometryOperations.Simplify` (name trap documented in `geoservices-compatibility.md` §2) |
| `buffer` | served | **Partial** | planar, single/multi distances, `quadrantSegments`; no `unit`/`bufferSR`/geodesic (`GeometryService.cs:Buffer`) |
| `intersect` | served | **Have** | `→ IGeometryOperations.Intersection` (disjoint→empty matches spec) |
| `simplify` (topological repair/MakeValid) | served | **Have** | `→ IGeometryProcessing.Repair` (NTS `GeometryFixer`) |
| `areasAndLengths`, `lengths` | served | **Have** | `→ IGeometryMeasures.Area/Length` |
| `distance` | served | **Have** | `→ IGeometryMeasures.Distance` (planar) |
| `convexHull` | served | **Have** | `→ IGeometryProcessing.ConvexHull` |
| `difference`, `union` | served | **Have** | `→ IGeometryProcessing.Difference/Union` |
| `relation` (DE-9IM `relationParam`) | served | **Have** | `→ IGeometryRelations.Relate` |
| `densify` | served | **Have** | `→ IGeometryProcessing.Densify` |
| `labelPoints` | served | **Have** | `→ IGeometryMeasures.LabelPoint` |
| `offset` | rejected, unadvertised | **Non-goal** | no engine verb; typed `invalid.arguments` (§7.1) |
| `cut` | rejected, unadvertised | **Non-goal** | same |
| `reshape` | rejected, unadvertised | **Non-goal** | same |
| `trimExtend` (`trim-extend/`) | rejected, unadvertised | **Non-goal** | same |
| `autoComplete` (`auto-complete/`) | rejected, unadvertised | **Non-goal** | same |
| `findtransformations` (datum-transformation lookup) | — | **Missing** | S2; curated EPSG catalogue (15 CRSs) + classic Helmert; no transformation-listing op |
| `fromGeoCoordinateString` / `toGeoCoordinateString` (MGRS/USNG/UTM notation) | — | **Missing** | S2; no coordinate-notation codec in tree |
| `datumTransformation` param on `project` | honestly rejected | **Partial** | rejected by name (`EsriFeatureQuery.cs` path for queries; geometry `project` likewise has no datum tables) |

Score: **14/21 served**, 5 edit-topology non-goals (no engine verb, rejected by
name), 2 notation/lookup gaps. The old "3 of 19" verdict in
`geoservices-compatibility.md` §2 predates ADR-0036 and is superseded by this
matrix.

## 2. Semantic traps (served but planar — must stay documented)

- Buffer/geodesic: ArcGIS buffers points geodesically in geographic CRSs and
  honours `unit`/`bufferSR`; ours is planar in the geometry's CRS. A
  `distances=1000&unit=9001` request is not reproducible without
  projection+units — currently 400 unless caller projects first.
- Measures are planar (XY); simplify preserves Z, algorithms ignore M.

## 3. Follow-ups (filed)

- T-I Geometry parity: `buffer` units (`unit` linear/angular) + `bufferSR`
  via transform-then-buffer; `findtransformations` honest listing of the
  curated catalogue; `from/toGeoCoordinateString` scope (likely new
  coordinate-notation codec or documented non-goal after demand check).
