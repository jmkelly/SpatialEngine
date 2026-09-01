# Capability Contract Catalog (distilled)

The versioned contracts shipped in `Spatial.PluginSdk`. Implements
ADR-0026/0027/0028. Shared error variant
`invalid.arguments` covers bad arguments **and** inputs the algorithm cannot
process; anything else is a provider failure. Argument names are stable.

## Geometry operations (`Spatial.PluginSdk.Operations`, provider `nts@1`)

| Contract | Input | Output | Behaviour |
| --- | --- | --- | --- |
| `spatial.geometry.buffer@1` | geometry, distance, optional quadrantSegments | geometry | OGC buffer; negative erodes |
| `spatial.geometry.intersection@1` | left, right | geometry | disjoint inputs succeed with empty result |
| `spatial.geometry.validate@1` | geometry | bool | invalid geometry = successful `false`, never a failure |
| `spatial.geometry.simplify@1` | geometry, tolerance | geometry | Douglas-Peucker; zero returns unchanged |

All four: **inline, cancellable, pure** (never job-routed by default).
Geometry crosses the wire as `{"$geometry":"<SGEOM base64>"}` — never JSON
geometry. NTS adapter notes: open rings closed before processing (core does
not require closure); algorithms are planar (computed results are XY; simplify
preserves Z); result carries input CRS (intersection: left's); validation
pre-checks OGC ring rules, then NTS `IsValidOp`.

## Transformations (`Spatial.PluginSdk.Transformations`, provider `projnet@1`)

| Contract | Input | Output | Behaviour |
| --- | --- | --- | --- |
| `spatial.crs.describe@1` | crs identity string (`EPSG:4326`) | structured `CrsDescription` | name, family, axes (name/orientation/unit), datum, ellipsoid |
| `spatial.coordinate.transform@1` | geometry, optional `source`, required `target` | geometry stamped with target CRS | out-of-area (non-finite) result = actionable error, never poisoned geometry |

- **Axis order: x-first for every CRS** (x = longitude/easting). Describe
  reports declared axes; the adapter performs no swaps — axis-order tests pin
  this. Z/M pass through untouched; empty geometries keep type and layout.
- Omitted `source` defaults to the geometry's own CRS (then required).
- Curated EPSG catalogue (12 CRSs, built programmatically — ProjNet's WKT
  reader mis-classifies Pseudo-Mercator). Accuracy documented: modern datums
  zero-shift (sub-mm vs PROJ); OSGB36 classic Helmert (±0.1 m, no grid).
- CRS descriptions cross the wire in the `{"$crs":…}` tag.

## Data providers (`Spatial.PluginSdk.Providers`, `postgis@1`, `demo@1`)

| Contract | Input | Output | Permission | Behaviour |
| --- | --- | --- | --- | --- |
| `spatial.catalogue.list@1` | optional LIKE `pattern` | bounded stream of JSON metadata items | — | one item per spatial dataset (id, schema, table, geometry column, SRID, row estimate) |
| `spatial.dataset.describe@1` | dataset id | bounded stream, one JSON item | — | fields in column order, geometry column + SRID/type, row estimate, identity columns |
| `spatial.dataset.create@1` | dataset id, **canonical batch bytes**, optional SRID | dataset id | `spatial.dataset.create` | table from batch schema; geometry column at SRID |
| `spatial.feature.scan@1` | dataset id | bounded stream of canonical batches | `spatial.feature.read` | streams every feature |
| `spatial.feature.query@1` | dataset id, optional bbox (all-or-none, x-first), optional filter | bounded stream of canonical batches | `spatial.feature.read` | bbox + parameterised attribute filtering |
| `spatial.feature.write@1` | dataset id, batch, optional transaction handle | appended count | `spatial.feature.write` | single-transaction append |
| `spatial.transaction.begin@1` / `commit@1` / `rollback@1` | — / handle / handle | handle / bool / bool | — | begin returns a runtime-owned handle; inactive handle = `invalid.arguments` |

**Interchange rules for all providers:**
- Feature data is **canonical binary both directions** (`FeatureBatchCodec` v1
  bytes; one stream item per batch) — identical on in-process and worker paths.
- Metadata is small **JSON text items** (the sanctioned JSON exception),
  written by the shared `DatasetMetadataJson` — never feature/geometry payloads.
- No new inline codec tags; streams ride `$resource` handles with
  `$bytes`/string items.
- Dataset identifiers: strict `schema.table` grammar (`[a-z_][a-z0-9_]*` per
  part; `public` default), validated, never concatenated raw into SQL;
  filter literals are always bound parameters.

**PostGIS adapter specifics:** EWKB ↔ core geometry is the plugin's only
`Spatial.Core.Geometry` surface (SRID flag ↔ `EPSG:<srid>`; SRID 0 = unknown
CRS); schema discovery via `geometry_columns` + `information_schema` +
`pg_class` + primary keys; writes via `ST_GeomFromEWKB(@p, srid)`; commands
run with the invocation token (DB-command cancellation); cancelled
scan/query fails the stream with `operation.cancelled`.

## Conformance (applies to every provider of every contract)

- The shared suite `tests/conformance/Spatial.Conformance.Tests` runs each
  contract's fixtures: success, empty input, unsupported input, cancellation,
  diagnostics — plus provenance assertions (capability, provider, step,
  duration) on every outcome.
- Geometry-carrying fixtures live **with the conformance suite** (tests/
  namespace), not in the SDK — `Spatial.Core.Geometry` fan-in discipline.
- The same invoker delegate drives the in-process provider and the packaged
  worker; both must agree on every result shape.
- Store-backed PostGIS behaviour lives in the containerised integration suite
  (`tests/integration/Spatial.PostGIS.Tests`, Testcontainers).
