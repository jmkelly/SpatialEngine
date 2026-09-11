# Service Contract Catalog (distilled)

The typed contracts in `Spatial.PluginSdk`. Implements ADR-0033
(replaces ADR-0026/0027/0028 capability contracts). Shared error codes:
`invalid.arguments`, `not.found`, `store.unavailable` (see `runtime.md`).

## Geometry operations (`IGeometryOperations`, NTS)

| Method | Input | Output | Behaviour |
| --- | --- | --- | --- |
| `Buffer` | geometry, distance, optional quadrantSegments (default 8) | geometry | OGC buffer; negative erodes |
| `Intersection` | left, right | geometry | disjoint inputs succeed with empty result |
| `Validate` | geometry | bool | invalid geometry = successful `false`, never a failure |
| `Simplify` | geometry, tolerance | geometry | Douglas-Peucker; zero returns unchanged |

All four: **pure, cancellable** (`CancellationToken`, honoured before the
algorithm runs). Geometry crosses as Base64 SGEOM — never JSON geometry.
NTS adapter notes: open rings closed before processing (core does not
require closure); algorithms are planar (computed results are XY; simplify
preserves Z); result carries input CRS (intersection: left's); validation
pre-checks OGC ring rules, then NTS `IsValidOp`.

## Geometry measures, processing and relations (`IGeometryMeasures`, `IGeometryProcessing`, `IGeometryRelations`, NTS)

Added by ADR-0036 so the GeoServices adapter maps protocol verbs without
holding algorithms. All verbs are pure, planar and cancellable.

| Interface | Method | Behaviour |
| --- | --- | --- |
| `IGeometryMeasures` | `Area`, `Length` | planar; 0 for shapes of the wrong dimension |
| `IGeometryMeasures` | `Distance` | planar minimum distance |
| `IGeometryMeasures` | `LabelPoint` | an interior point |
| `IGeometryProcessing` | `Union`, `Difference` | set operations |
| `IGeometryProcessing` | `ConvexHull` | hull of all inputs |
| `IGeometryProcessing` | `Repair` | topological MakeValid (NTS `GeometryFixer`); **not** Douglas-Peucker |
| `IGeometryProcessing` | `Densify` | segment length cap |
| `IGeometryRelations` | `Relate` | DE-9IM intersection pattern |

## Transformations (`ICrsDirectory`, `ICoordinateTransforms`, ProjNet)

| Method | Input | Output | Behaviour |
| --- | --- | --- | --- |
| `Describe` | crs identity string (`EPSG:4326`) | structured `CrsDescription` | name, family, axes (name/orientation/unit), datum, ellipsoid |
| `Transform` | geometry, optional `source`, required `target` | geometry stamped with target CRS | out-of-area (non-finite) result = actionable error, never poisoned geometry |

- **Axis order: x-first for every CRS** (x = longitude/easting). Describe
  reports declared axes; the service performs no swaps — axis-order tests pin
  this. Z/M pass through untouched; empty geometries keep type and layout.
- Omitted `source` defaults to the geometry's own CRS (then required).
- Curated EPSG catalogue (15 CRSs). Accuracy: modern datums zero-shift
  (sub-mm vs PROJ); OSGB36 classic Helmert (±0.1 m, no grid).

## Data stores (`IDataCatalogue`, `IFeatureStore`, `IFeatureEditStore`, `ITransactionStore`, `IDemoJobs`)

| Method | Input | Behaviour |
| --- | --- | --- |
| `ListAsync` | optional LIKE `pattern` | one `DatasetSummary` per spatial dataset (id, schema, table, geometry column, SRID, row estimate) |
| `DescribeAsync` | dataset id | full `DatasetDescription` (fields in column order, geometry column + SRID/type, row estimate, identity columns) |
| `CreateAsync` | dataset id, **sample batch**, SRID | table from batch schema; geometry column at SRID |
| `ScanAsync` | dataset id | every feature as `FeatureBatch` pages |
| `QueryAsync` | dataset id, optional bbox (all-or-none, x-first), optional filter | bbox + parameterised attribute filtering |
| `WriteAsync` | dataset id, batch, optional transaction handle | single-transaction append, returns count |
| `AddAsync` / `UpdateAsync` / `DeleteAsync` (`IFeatureEditStore`) | dataset id, batch (or feature ids), optional transaction handle | per-feature `FeatureEditOutcome` in input order; additive capability, implemented by PostGIS only (ADR-0037) |
| `Begin/Commit/RollbackAsync` | — / handle / handle | store-owned string handles; unknown handle = `invalid.arguments` |
| `SleepAsync` | milliseconds, progress | demo-only cancellable delay |

**Interchange rules:**
- Feature data is **canonical `FeatureBatch` pages** (ADR-0020); on HTTP as
  Base64 SFBAT strings.
- Metadata is **JSON DTOs** — never feature/geometry payloads.
- Dataset identifiers: strict `schema.table` grammar (`[a-z_][a-z0-9_]*` per
  part; `public` default), validated, never concatenated raw into SQL;
  filter literals are always bound parameters.

**PostGIS specifics:** EWKB ↔ core geometry is the store's only
`Spatial.Core.Geometry` surface (SRID flag ↔ `EPSG:<srid>`; SRID 0 = unknown
CRS); schema discovery via `geometry_columns` + `information_schema` +
`pg_class` + primary keys; writes via `ST_GeomFromEWKB(@p, srid)`; commands
run with the caller's token (DB-command cancellation). Editing (ADR-0037)
adds `UPDATE`/`DELETE`/`INSERT … RETURNING` built from discovered identities
and back to the per-feature `FeatureEditOutcome`; it is available only when
the table has a primary key.

**Demo specifics:** read-only procedural datasets (110-point grid + 8
cities, EPSG:4326); bbox queries only (attribute filters rejected);
writes/creation/editing rejected.

**ArcGIS REST specifics:** read-only remote provider; writes, creation and
editing rejected.
