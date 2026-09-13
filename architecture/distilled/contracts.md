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

## Data stores (`IDataCatalogue`, `IFeatureStore`, `IFeatureLookup`, `IFeatureEditStore`, `ITransactionStore`, `IDatasetIngest`, `IStoreRegistry`, `IMapRegistry`, `IDemoJobs`)

`IStoreRegistry` is the one runtime-keyed seam (ADR-0033): a store name
resolves to its catalogue, feature store and additive capabilities, so a
protocol adapter reads a mixed map's layers from their own stores without
holding the DI container. Required reads are `invalid.arguments` for an
unknown store; additive capabilities return `null`.

| Method | Input | Behaviour |
| --- | --- | --- |
| `ListAsync` | optional LIKE `pattern` | one `DatasetSummary` per spatial dataset (id, schema, table, geometry column, SRID, row estimate) |
| `DescribeAsync` | dataset id | full `DatasetDescription` (fields in column order, geometry column + SRID/type, row estimate, identity columns) |
| `CreateAsync` | dataset id, **sample batch**, SRID | table from batch schema; geometry column at SRID |
| `ScanAsync` | dataset id | every feature as `FeatureBatch` pages |
| `QueryAsync` | dataset id, optional bbox (all-or-none, x-first), optional filter | bbox + parameterised attribute filtering |
| `WriteAsync` | dataset id, batch, optional transaction handle | single-transaction append, returns count |
| `AddAsync` / `UpdateAsync` / `DeleteAsync` (`IFeatureEditStore`) | dataset id, batch (or feature ids), optional transaction handle | per-feature `FeatureEditOutcome` in input order; additive capability, implemented by PostGIS only (ADR-0037) |
| `GetAsync` (`IFeatureLookup`) | dataset id, feature ids | features found by identity (miss = absent, not an error); additive read-by-identity capability, implemented by PostGIS only (ADR-0038) |
| `IngestAsync` (`IDatasetIngest`) | `IngestRequest`, `FeatureBatch` pages | atomic create + load in one transaction; identity mode `None`/`Auto`/`Source`; additive capability (ADR-0041). The host ingest route also accepts `sourceSrid` and reprojects the decoded pages through `ICoordinateTransforms` before load |
| `ListAsync` / `GetAsync` / `PutAsync` / `DeleteAsync` (`IMapRegistry`) | map name / `Map` | runtime map registry (ADR-0053, evolving ADR-0041): declared entries immutable, runtime entries persisted; `Map` carries name, store, stable-id layers and the enabled `Services` (Feature/Map/Tiles/Wms/Wfs/Image); each layer may carry a persisted MapLibre style fragment (ADR-0047) and a `Kind` (feature/image) with an optional per-layer store |
| `Begin/Commit/RollbackAsync` | — / handle / handle | store-owned string handles; unknown handle = `invalid.arguments` |
| `SleepAsync` | milliseconds, progress | demo-only cancellable delay |

## Raster imagery (`IRasterCatalogue`, NetVips)

Added by ADR-0051 so the GeoServices ImageServer (spec §8) can read a raster
without any raster value crossing a contract. Implemented by
`Spatial.Imagery.Vips`; core-typed, package-free.

| Method | Input | Behaviour |
| --- | --- | --- |
| `DescribeAsync` | dataset | `RasterDatasetDescription`: core `RasterInfo` (extent, CRS identity, pixel size, dimensions, band count, pixel type, block size and pyramid levels from a tiled/pyramidal file, optional stored band statistics) and, when a catalog exists, its integer `ObjectIdField` + core `FeatureSchema` |
| `ListItemsAsync` | dataset | catalog items: integer identity, core geometry footprint, per-item raster info, attributes in schema order; empty for a single-raster service |
| `IdentifyAsync` | dataset, `RasterIdentifyRequest` | sampled pixel values at the geometry centroid plus the overlapping catalog items; works without a catalog |
| `ExportAsync` | dataset, `RasterExportRequest` | warped/encoded `RasterImage` over a `RasterViewport`; resampling kernel, target pixel type, nodata transparency, quality; an optional `RasterId` selects one catalog item; a downscale reads the coarsest internal overview that still covers the output |
| `ListFilesAsync` | dataset, optional `rasterId` | opaque `RasterFile` descriptors (`Id`, `Name`, `MediaType`, `Size`) for a catalog item or the whole dataset (spec §8.0.7) |
| `ReadFileAsync` | dataset, `RasterFile.Id` | raw file bytes (`RasterFileContent`); the provider validates the opaque id against its own files (spec §8.5) |

**Raster interchange rules:** only encoded image bytes (`RasterImage`),
core metadata, core `IGeometry` footprints and core `AttributeValue`
attributes cross. The identify response adds one scalar sample per band at
the identified point (spec §8.0.6); it is a value tuple, never a raster/band
model. Raw download crosses as `RasterFile`/`RasterFileContent` (bytes plus
media type, name and size), keyed by an opaque provider-owned id; a path,
file handle, NetVips/GDAL type or GeoTIFF tag never crosses. A tiled,
internally-overviewed (pyramidal) GeoTIFF — a COG — is read through
`tile-width`/`tile-height`/`n-subifds` and exported from the chosen overview;
`RasterInfo` reports those as block size and pyramid levels. Writing a
COG-style file is a concrete `VipsRasterCatalogue.WriteCogAsync` storage
operation, not a contract verb. Pixel-type
conversion is limited to the real integer/float formats and rejects
complex/sub-byte types (a colour raster stays 8-bit). Georeferencing is
descriptor-supplied on the managed NetVips path; GDAL is the measured-demand
upgrade. `RasterFormat`/`RasterPixelFormat`/`RasterBlend`/`RasterViewport`/
`RasterBuffer`/`RasterImage` keep the ADR-0044 render vocabulary.

**Interchange rules:**
- Feature data is **canonical `FeatureBatch` pages** (ADR-0020); on HTTP as
  Base64 SFBAT strings.
- Metadata is **JSON DTOs** — never feature/geometry payloads.
- `Map`/`MapService`/`MapLayer`/`MapLayerKind` and the ingest records
  are core-typed (ADR-0053); no protocol or provider type crosses.
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
the table has a primary key. Read-by-identity (ADR-0038) adds a targeted
`SELECT … WHERE <identity> = @p…` (one bound parameter per identity value,
`LIMIT 1` for a single lookup) so GeoServices edit merges no longer scan the
dataset; a table without a primary key returns an empty result.

**Demo specifics:** read-only procedural datasets (110-point grid + 8
cities, EPSG:4326); bbox queries only (attribute filters rejected);
writes/creation/editing rejected.

**Memory specifics (ADR-0042):** the ephemeral writable in-memory store
(keyed `memory`, always available) implements the writable faces including
`IDatasetIngest`, `IFeatureEditStore` and `IFeatureLookup`. State is
process-local and non-durable; an `Auto`/`Source` dataset is editable and
lookup-able, a `None` dataset is query-only; bbox queries only (attribute
filters rejected).

**Map registry specifics (ADR-0053):** `Spatial.Provider.Maps`
composes immutable declared entries (config-seeded, whole-store entries
expand to the sorted dataset list with stable ids) with runtime entries in a
versioned JSON file written atomically, and reads a pre-ADR-0053
`publications.json` once for migration. `MapLayer` optionally carries
`Style`, a JSON array of MapLibre style-layer objects in the ADR-0044 subset,
persisted verbatim (ADR-0047); the dataset is not repeated inside it — the
host injects `source-layer` when it assembles a render document. The OGC
WMS/WFS projection lives in `Spatial.Adapter.Ogc`, reads the same map and
keyed stores, and adds no contract: it renders through `IMapRenderer` and
queries through `IFeatureStore`. An enabled
service must be fed by a layer of the matching `Kind`. `Auto`-identity
ingest adds a
`GENERATED BY DEFAULT AS IDENTITY` primary key; `Source` uses a named integer
field; `None` is data-only. Ingest declares each geometry column with the
data's coordinate layout (XY, XYZ, XYM or XYZM), so Z and M are preserved; a
column mixing layouts is `invalid.arguments`. `FeatureId.Unassigned` on an add
means the store assigns the key (ADR-0043).

**ArcGIS REST specifics:** read-only remote provider; writes, creation and
editing rejected.
