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
15 ops), `GeoServicesEndpoints.Geometry.cs` (routes), SDK
`IGeometryOperations`/`IGeometryMeasures`/`IGeometryProcessing`/`IGeometryRelations`
(ADR-0036), `ICoordinateTransforms`/`ICrsDirectory`, wire `EsriUnits`
(`Spatial.Esri.Codec`).

## 1. Operations (21)

| ArcGIS op | Ours | Status | Evidence |
|---|---|---|---|
| `project` | served | **Have** | `GeometryService.cs:Operations["project"]` → `ICoordinateTransforms.Transform` |
| `generalize` (Douglas-Peucker) | served | **Have** | `→ IGeometryOperations.Simplify` (name trap documented in `geoservices-compatibility.md` §2) |
| `buffer` | served | **Partial** | planar transform-then-buffer: `unit` (curated `EsriUnits` table, linear+angular) + `bufferSR`/`outSR`/`inSR` chaining per spec §7.0.6; `geodesic=false` accepted as planar, `geodesic=true`/`unionResults` rejected (`GeometryService.cs:Buffer`) |
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
| `findtransformations` (datum-transformation lookup) | served | **Have** | S2; curated EPSG catalogue (15 CRSs) + classic Helmert; served: same datum returns `[]`, a datum step returns one forward `{geoTransforms:[{name,transformForward:true,method}]}` composite with the OSGB36 classic-Helmert note; `vertical=true`/non-empty `extentOfInterest` rejected; `numOfResults` honoured |
| `fromGeoCoordinateString` / `toGeoCoordinateString` (MGRS/USNG/UTM notation) | rejected, unadvertised | **Non-goal** | S2 lists 8 conversion types each (MGRS/USNG/UTM/GeoRef/GARS/DMS/DDM/DD) with modes; no codec in tree, no engine verb: reject by name, unadvertised, never half-parse (non-goal, §7.1) |
| `datumTransformation` param on `project` | honestly rejected | **Partial** | rejected by name (`GeometryService.cs:Project` and `EsriFeatureQuery.cs` for queries; the engine has no datum tables, so a supplied transformation fails instead of projecting silently without it) |

Score: **15/21 served**, 5 edit-topology non-goals + 1 notation non-goal row
(no engine verb, rejected by name, unadvertised). The old "3 of 19" verdict in
`geoservices-compatibility.md` §2 predates ADR-0036 and is superseded by this
matrix.

## 2. Semantic traps (served but planar — must stay documented)

- Buffer/geodesic: ArcGIS buffers points geodesically in geographic CRSs and
  honours `unit`/`bufferSR`; ours is planar transform-then-buffer.
  `distances=1000&unit=9001` against a 4326 geometry with a projected
  `bufferSR` reproduces the projected result (transform to the buffer CRS,
  planar buffer in metres, transform to `outSR`). A linear `unit` against a
  geographic buffer CRS stays 400 (no geodesic verb: name a projected
  `bufferSR`); an angular `unit` (9101/9102) buffers planar degrees.
- Measures are planar (XY); simplify preserves Z, algorithms ignore M.

## 3. Follow-ups (landed as T-044)

- T-I Geometry parity: `buffer` units (`unit` linear/angular) + `bufferSR`
  via transform-then-buffer; `findtransformations` honest listing of the
  curated catalogue; `from/toGeoCoordinateString` scope (likely new
  coordinate-notation codec or documented non-goal after demand check).
Outcome: buffer units + findtransformations served; notation recorded as a
deliberate non-goal (no MGRS/USNG/UTM codec demand justifies a new package +
engine verb; half-parsing notations is explicitly out).
