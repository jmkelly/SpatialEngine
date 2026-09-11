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

## Data stores (`IDataCatalogue`, `IFeatureStore`, `ITransactionStore`, `IDemoJobs`)

| Method | Input | Behaviour |
| --- | --- | --- |
| `ListAsync` | optional LIKE `pattern` | one `DatasetSummary` per spatial dataset (id, schema, table, geometry column, SRID, row estimate) |
| `DescribeAsync` | dataset id | full `DatasetDescription` (fields in column order, geometry column + SRID/type, row estimate, identity columns) |
| `CreateAsync` | dataset id, **sample batch**, SRID | table from batch schema; geometry column at SRID |
| `ScanAsync` | dataset id | every feature as `FeatureBatch` pages |
| `QueryAsync` | dataset id, optional bbox (all-or-none, x-first), optional filter | bbox + parameterised attribute filtering |
| `WriteAsync` | dataset id, batch, optional transaction handle | single-transaction append, returns count |
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
run with the caller's token (DB-command cancellation).

**Demo specifics:** read-only procedural datasets (110-point grid + 8
cities, EPSG:4326); bbox queries only (attribute filters rejected);
writes/creation rejected.
