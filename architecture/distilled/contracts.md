# Service Contract Catalog (distilled)

The typed contracts in `Spatial.Contracts`. Implements ADR-0033
(replaces the ADR-0026/0027/0028 versioned worker contracts). Shared error codes:
`invalid.arguments`, `not.found`, `store.unavailable` (see `runtime.md`).

## Vector tiles (`IVectorTileService`, MVT)

| Method | Input | Output | Behaviour |
| --- | --- | --- | --- |
| `RenderAsync` | `VectorTileRequest` (tile bounds/CRS, extent and resolved `VectorTileLayer`s) | `VectorTile` | Cancellable MVT 2.1 bytes; queries each layer, reprojects through `ICoordinateTransforms`, and never exposes protobuf types. Rejects a tile whose bounds have no extent on an axis, whose CRS is blank or whose extent is outside 1..65536 as `invalid.arguments`. |

`ITileCache` also carries the MVT bytes through its optional
`TryGetVectorAsync` / `SetVectorAsync` faces, preserving the same ownership
and bounds as raster entries (ADR-0046/0070).

## Geometry operations (`IGeometryOperations`, NTS)

| Method | Input | Output | Behaviour |
| --- | --- | --- | --- |
| `Buffer` | geometry, distance, optional quadrantSegments (default 8) | geometry | OGC buffer; negative erodes |
| `Intersection` | left, right | geometry | disjoint inputs succeed with empty result |
| `Validate` | geometry | bool | invalid geometry = successful `false`, never a failure |
| `Simplify` | geometry, tolerance | geometry | Douglas-Peucker; the tolerance is an algorithm parameter, so it may empty a small ring |
| `Generalize` | geometry, maxDisplacement | geometry | Douglas-Peucker at a deviation *allowance* (ADR-0079): returns input vertices only, keeps every input vertex within the allowance, and returns the input unchanged when the allowance cannot be spent without changing the geometry's kind (or is zero) |

All five: **pure, cancellable** (`CancellationToken`, honoured before the
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
| `IGeometryMeasures` | `Centroid` | centre of mass (area for polygons, length for lines); **not** the envelope middle; empty point for empty input (ADR-0085) |
| `IGeometryProcessing` | `Union`, `Difference` | set operations |
| `IGeometryProcessing` | `ConvexHull` | hull of all inputs |
| `IGeometryProcessing` | `Repair` | topological MakeValid (NTS `GeometryFixer`); **not** Douglas-Peucker |
| `IGeometryProcessing` | `Densify` | segment length cap |
| `IGeometryRelations` | `Relate` | DE-9IM intersection pattern: nine cells, each `T`/`F`/`0`/`1`/`2`/`*`; the grammar is `Spatial.Core.Geometry.De9imPattern` and anything else is `invalid.arguments` (ADR-0036, SpatialEngine-imj) |

## Ground-distance buffering (`IGeodesicBuffering`, ProjNet)

Added by ADR-0075 so a linear distance against a geographic CRS means
metres on the ground rather than degrees on a plane. Pure and cancellable.

| Method | Input | Output | Behaviour |
| --- | --- | --- | --- |
| `Buffer` | geometry, distance in **metres**, optional quadrantSegments (default 8) | geometry in the input's CRS | negative erodes; the geometry must carry a geographic CRS |

- Reproject-and-buffer onto a local transverse Mercator (unit scale, centred
  on the work, on the source's own datum), planar buffer, project back.
  **Stated tolerance: 0.05% relative for a working radius (the part's
  envelope half-diagonal plus the distance) up to 300 km**
  (`WorkingRadiusLimitMetres`, `StatedRelativeTolerance`).
- Past the limit, within 89° of a pole, without a CRS, or against a
  projected CRS: `invalid.arguments` naming the reason. Never a silently
  wrong shape.
- Each part of a multi-part geometry gets its own plane, then the parts are
  dissolved — so the tolerance follows the part's extent, not the
  collection's.

## Transformations (`ICrsDirectory`, `ICoordinateTransforms`, ProjNet)

| Method | Input | Output | Behaviour |
| --- | --- | --- | --- |
| `Describe` | crs identity string (`EPSG:4326`) | structured `CrsDescription` | name, family, axes (name/orientation/unit), datum, ellipsoid |
| `FindTransformations` | `CrsTransformationQuery` (source, target, optional area of interest) | ranked `CrsTransformation` list | candidates with steps, Helmert or grid parameters, area of use and derived accuracy; same datum → empty; the first candidate is the path `Transform` applies (ADR-0087, ADR-0107) |
| `Transform` | geometry, optional `source`, required `target` | geometry stamped with target CRS | out-of-area (non-finite) result = actionable error, never poisoned geometry |

- **Axis order: x-first for every CRS** (x = longitude/easting). Describe
  reports declared axes; the service performs no swaps — axis-order tests pin
  this. Z/M pass through untouched; empty geometries keep type and layout.
- Omitted `source` defaults to the geometry's own CRS (then required).
- Built-in EPSG catalogue, **defined in EPSG WKT** and read by the provider's
  own reader (`ProjWkt`), so a definition is data and construction stays on
  one programmatic builder: the common geographic and projected CRSs, plus
  projected *families* generated from one WKT template — the UTM grid (zones
  1-60 north, EPSG 32601-32660, and 1-60 south, 32701-32760) and the ETRS89
  and NAD83 UTM bands over their own datums. WKT1 and WKT2 both read; the
  Pseudo-Mercator spellings are intercepted and routed to the known-good
  construction (reading EPSG:3857 as `Mercator_1SP` is 33 km out). A code
  outside the catalogue and the families is `invalid.arguments`; there is no
  WKT *input*. Accuracy: modern datums zero-shift (sub-mm vs PROJ); OSGB36
  classic Helmert (±0.1 m), or a deployed datum shift grid (ADR-0105). The
  catalogue is built once, and each
  definition read and each CRS built on first use.
- **A definition is WKT text; a datum's accuracy and area of use are not part
  of it** (ADR-0086). The definition carries the datum's `TOWGS84` shift —
  the construction input ProjNet needs. How accurate that shift is, and over
  what ground it means anything, are attributes of the *operation* EPSG
  registers against the datum, and WKT states neither. They live in a curated
  `EpsgDatumOperations` table beside the WKT, keyed on the datum name the
  `DATUM` node carries, and joined to a definition to form the graph's datum
  node. A datum with no published operation contributes no node rather than an
  invented accuracy. Both halves have exactly one home, so they cannot drift.
- **An area of use is a set of rectangles** (ADR-0111). EPSG publishes
  extents that cross the antimeridian as a west bound in the east and an east
  bound in the west — extent 1175 "New Zealand" is 160.6E to 171.2W — and
  read as one rectangle that is the empty box, which the graph means by "valid
  nowhere", so the datum drops out of the graph rather than the part of it that
  does not overlap. The area is `CrsAreaOfUse(Name, Boxes)`, the wrapped
  extent is its two rectangles, and emptiness is the absence of boxes. The
  graph's intersection and union are rectangle algebra over that set and merge
  nothing: joining the two halves of a wrapped extent would fabricate the very
  rectangle this removes. `findTransformations` publishes `areaOfUse` as a
  list of envelopes for the same reason, and an `extentOfInterest` across the
  seam is split rather than sorted into one interval. The area's name stays a
  short label — the registry's verbatim wording stays with the row, because
  half the areas in a listing are composed ones that name no extent.
- **A deployed datum shift grid is applied per coordinate** (ADR-0105,
  ADR-0107). Grids are configured, not embedded (`Spatial:Grids:Directories`,
  a priority order), read as NTv2, and cached once found. Where a grid covers a
  coordinate the shift is the grid's; where it does not — off its block, or for
  a datum no bundle serves — the classic Helmert stands, bit for bit as before.
  The choice is per coordinate, not per request, so a geometry crossing a
  block edge is shifted by both in one request, and nothing is extrapolated
  across an edge. A host with no bundle deployed is exactly the host it was,
  and the first ranked candidate is the applied path for a grid as well as a
  Helmert.

## Data stores (`IDataCatalogue`, `IFeatureStore`, `IFeatureLookup`, `IFeatureEditStore`, `ITransactionStore`, `IDatasetIngest`, `IVersionedFeatureStore`, `IStoreRegistry`, `IMapRegistry`, `IDemoWork`)

`IStoreRegistry` is the one runtime-keyed seam (ADR-0033): a store name
resolves to its catalogue, feature store and additive faces, so a
protocol adapter reads a mixed map's layers from their own stores without
holding the DI container. Required reads are `invalid.arguments` for an
unknown store; additive faces return `null`.

**Content versions (ADR-0083).** A store may implement
`IVersionedFeatureStore.GetContentVersionAsync(dataset)` and report an opaque
token that moves whenever that dataset's feature content changes;
`ContentVersions.OfAsync` returns it, or `ContentVersions.Unversioned` for a
store that does not, and `ContentVersions.FoldAsync(stores, datasets)` folds a
render's datasets into the one token the tile cache key carries. The version
is not an existence check (an unknown dataset reports the unversioned token
and still fails at read time) and callers never parse it.

A durable store keeps the token in the database, not in process: the
PostGIS and SQL Server stores report a counter row bumped inside the
write's own transaction, so every host reading that database sees the
same version (ADR-0129). A dataset the engine has never written has no
row and reports the unversioned token — as does a read that cannot see
the counter at all, so a locked-down reader degrades to the old
behaviour rather than failing a render.

**Spelling and ladder.** `Catalogue` = datasets in one store
(`IDataCatalogue`, `IRasterCatalogue`, `GET /api/catalogue`); `Registry` =
stores and maps across the engine (`IStoreRegistry`, `IMapRegistry`).
Esri-protocol catalog concepts keep Esri's `Catalog` spelling
(`GeoServicesCatalog`, raster catalog items) — both spellings are deliberate.

**The feature query plan (ADR-0074, ADR-0098, implemented).** The
feature read is a plan value, `Spatial.Contracts.FeatureQuery`:
identity restriction, a `Predicate` tree over `FieldRef`/`Literal`, an
optional `BoundingBox`, a projection field list, an `Order` (with the store
appending the identity tie-break so paging is stable), `Limit`/`Offset` and an
optional opaque `Cursor`; the read returns
`FeatureQueryPage(Batches, NextCursor, TotalCount?)` where a null
`TotalCount` means "not computed". Reductions are an additive
`IFeatureAggregateStore` face (`CountAsync`/`DistinctAsync`/`AggregateAsync`),
the ADR-0033 optional-capability pattern, so a store without it still answers
reads correctly; `FeatureReductionFallback` reduces for a caller whose store
has no such face. Pushdown is per-conjunct and best-effort: a provider pushes
what its dialect can express and evaluates the residual in memory, so a valid
plan is never refused for a dialect gap and the result always equals
evaluating the plan over the whole dataset. A reduction's row order is the order
the plan asked for, so a store offers a grouped reduction only when the plan
requests an order — and the GeoServices adapter offers a grouped
`outStatistics` only when `orderByFields` names exactly the group fields, the
case where the group keys are the total order the store can return; every other
statistics request keeps the scan-and-match path and the same answer (ADR-0098
§7 as amended by SpatialEngine-u2x.9.2). An **ungrouped** reduction has one
group, so its order cannot differ and it is always offered; a **grouped** one
is offered only when the plan's order is over the group key itself, and is
reduced in the caller otherwise. Three rules about the values are the
reference's, and a store that reduces in its own dialect is measured against
them (ADR-0115): a statistic with no non-null input is a **null** — the count
included, so a `COUNT(field)` of zero is a null and not a zero — the sample
forms `var`/`stddev` are **null** for fewer than two values rather than zero,
and an ungrouped reduction of an empty set is **one group of nulls** where a
dialect returns no row at all — except the **row count**, which counts rows
rather than values and is a **zero** (ADR-0098 §3, ADR-0131). A page of an
ungrouped reduction that lands past its one group is **no groups**, not that
one group (ADR-0131). One statistic is not a number: `Envelope`
reduces a geometry field to the smallest rectangle over its non-null
geometries, reported as an `AttributeKind.Envelope` value (a reduced kind no
field may declare, ADR-0120). The page over groups and `having` are the
**reduction's** own members — `AggregateQuery.Having`/`Limit`/`Offset` and
`AggregatePage.HasMore` (ADR-0128) — because a cap the plan carried would cut
rows the store never grouped. `Having` is the same `Predicate` vocabulary as the
plan's `where`, over the *group row* (the group fields and the statistics'
result names), and a store that implements the reduction face must answer both;
Postgres writes the clause as its own aggregate expression in the grouped
statement's `HAVING`, before the `LIMIT`. The one definition of that
evaluation is `Spatial.Querying`: `FeaturePlanExecutor` selects, orders, pages
and projects, `FeaturePlanExecutor.Finish` is the shaping half for a store that
already applied the restriction, and `ReferencePredicate` is the one predicate
evaluator every store and every test double goes through. A plan is validated
once, against the dataset's schema, by `FeatureQueryValidation` at the boundary
that received it. The one filter text in the system
is the published `filter` query parameter on `GET /api/features/query`, parsed
once at the boundary into a `Predicate`; the per-provider filter lexers,
parsers and SQL builders are retired, and the Esri `where` grammar compiles to
the same tree instead of evaluating features. That route also takes the plan
itself as JSON — `plan` beside the `filter`/`bbox` sugar, answered with the page
(`batches`, `nextCursor`, `totalCount`, `hasMore`) — with the predicate tree as
a node discriminated by `op` and every member an `op` does not carry refused by
name (ADR-0158); the text is deprecated, not removed. The plan's spatial
component stays the `BoundingBox` pre-filter: the DE-9IM `spatialRel` verbs
remain an
adapter-side verb (ADR-0036). Predicate *evaluation* is implementation code
and never enters `Spatial.Core`.

Two rules keep the back ends answering the same rows for the same plan
(ADR-0097). An attribute clause is pushed down to a store only when the
layer's `OBJECTID` is store-derived (an integer identity column); on a layer
whose `OBJECTID` is the scan ordinal (ADR-0037) the clause stays a residual
per-feature match, because a store that returns only the matching rows would
renumber the key. The same rule holds one level down, inside the store: a
PostGIS or SQL Server dataset with no identity column names its features by the
ordinal of the read, so a `WHERE` that reached SQL would renumber them, and
such a dataset keeps its restriction in the caller and selects over the whole
read. And a literal binds as its **column's** kind, never its
own: a guid-formatted string binds as a `Guid`, a number against a date-time
column binds as the instant it already is, and a pair that means nothing to
the reference evaluator (`uuid = 5`, `bit < true`, `LIKE` on a non-text
column) compiles to the constant the evaluator already answers rather than
coercing or failing. Which pairs are answerable at all is one table,
`PredicateCompatibility`, which is structural and lives in Core. One text
comparison **states** its case behaviour instead of inheriting one (ADR-0132):
`ILIKE` is the same whole-value pattern test as `LIKE` with the value and the
pattern folded over the ASCII alphabet, and each dialect writes that fold out —
`translate` on Postgres, `TRANSLATE` under the binary collation on SQL Server —
because `ILIKE` and a case-insensitive collation are each a *locale's*, and
answer differently on a `C` database and a stock one. It is a separate
comparison from `LIKE` because folding for a **search** is not folding for a
**key**: `delta` and `Delta` remain two features under every byte-ordered
comparison (ADR-0126). A plan whose only text comparison states its own fold
does not read the database's collation at all, and the Esri `where` grammar does
not grow the comparison — a plan carrying one narrows to what a remote service
can be asked about, because the remote restriction is a pre-filter over a read
the reference executor finishes.

**The match envelope is a plan too (ADR-0110, implemented).** The GeoServices
feature match compiles onto the same plan rather than reading the layer and
filtering it in the adapter: `objectIds` becomes `FeatureQuery.Ids` (the
layer's own identity column values), the `where` clause and the `time` extent
become `Where` — `time` as `observed IS NULL OR (observed >= start AND
observed <= end)` per date field, because the facade's rule keeps a feature
with no date value and drops only the rows whose every date is outside the
window — and the query geometry's envelope, already in the layer CRS, becomes
`BoundingBox`. The compilation is all-or-nothing and decided from the request
and the layer's identity model before any store is asked, so there is no
scan after a pushdown attempt. Three requests are never compiled and are still
refused or scanned: a layer whose `OBJECTID` is the scan ordinal (ADR-0097), a
`uniqueIds` request (the refusal is per feature, and a restricted read could
return none to refuse on) and an unsupported `spatialRel` (the same reason).
The pushed rows are a *pre-filter*: the in-memory matcher still decides each
of them, so it is the verification path rather than a fallback. Every served
`spatialRel` implies an intersection, so one box serves all of them.

**The map and per-feature surfaces use the same faces (ADR-0112,
implemented).** Each of the five remaining whole-layer reads in the GeoServices
adapter moved onto a face the tree already had, and each keeps the part that
has to stay: MapServer `identify` pushes only the query envelope's box (and
only on a layer whose `OBJECTID` is store-derived — its `layerDefs` clause
resolves the synthetic `OBJECTID` against the scan ordinal), so the
intersection test and the filters stay the answer; MapServer `find` pushes the search text
as one folded pattern per searched field (`%text%` for contains, `text%` for
startsWith) on the vocabulary's case-folding comparison, whose fold every back
end states identically, and keeps its own case-insensitive match as the answer —
a text outside the ASCII alphabet that fold covers, or one carrying a
backslash, keeps the narrower "a searched field is not null" restriction the
`LIKE` it replaced could not beat (ADR-0132);
`generateRenderer` asks `IFeatureAggregateStore` for the minimum and maximum
behind the class breaks and for the distinct set behind the unique values,
keeping the quantisation in the adapter, and keeps the scan when the `where`
clause is one no store can read; the layer extent is the aggregate
vocabulary's envelope statistic over the geometry column, one store aggregate
and no row read behind it (ADR-0120), and a table layer's empty extent is not
read at all. The per-feature `returnExtentOnly` query is the same reduction
over the match, and a reprojecting `outSR` keeps the match path, because the
union of the reprojected geometries is not the reprojected union. The per-feature (object) resource
and the attachment targets resolve through `IFeatureLookup`, keyed by the
`OBJECTID` each row **carries** rather than by the id the lookup was asked
with. A store therefore misses only if it keys `Feature.Id` by something other
than the identity column, and the scan decides rather than the wrong feature
being served; the engine's own ingest paths do not do that, since a stored
feature's `Feature.Id` is the identity column's value (ADR-0119).

| Method | Input | Behaviour |
| --- | --- | --- |
| `ListAsync` | optional LIKE `pattern` | one `DatasetSummary` per spatial dataset (id, schema, table, geometry column, SRID, row estimate) |
| `DescribeAsync` | dataset id | full `DatasetDescription` (fields in column order, geometry column + SRID/type, row estimate, identity columns, and the coordinate layout the store declares for the geometry column — ADR-0084) |
| `CreateAsync` | dataset id, **sample batch**, SRID | table from batch schema; geometry column at SRID |
| `ScanAsync` | dataset id | every feature as `FeatureBatch` pages |
| `QueryAsync` | dataset id, `FeatureQuery` plan (optional `Ids`, `Where` predicate, `BoundingBox`, `Projection`, `Order`, `Limit`, `Offset`, `Cursor`) | the plan's page: a `FeatureQueryPage(Batches, NextCursor?, TotalCount?, HasMore)`. `HasMore` is the explicit "one more" signal and a page that reports more always carries the `NextCursor` that reaches it; `TotalCount` is nullable and `null` means *not computed*, never zero. `Offset` and `Cursor` are alternatives, never composed; a store appends the feature identity as a final ascending sort key, so the total order is deterministic (ADR-0098). A store reads the page whenever the page has a position — an empty restriction is a whole-table read *with* a `LIMIT` — and a store that cannot push the plan still returns one page, not the match set (ADR-0116) |
| `WriteAsync` | dataset id, batch, optional transaction handle | single-transaction append, returns count |
| `AddAsync` / `UpdateAsync` / `DeleteAsync` (`IFeatureEditStore`) | dataset id, batch (or feature ids), optional transaction handle | per-feature `FeatureEditOutcome` in input order; additive face, implemented by PostGIS only (ADR-0037) |
| `GetAsync` (`IFeatureLookup`) | dataset id, feature ids | features found by identity (miss = absent, not an error); the key is the dataset's identity column's value, which is what a stored feature's `Feature.Id` is on an identity-backed dataset (ADR-0119); additive read-by-identity face implemented by every writable store — memory, PostGIS and SQL Server (ADR-0038). A dataset that declares **no** identity column is a typed `invalid.arguments` naming the dataset, whatever ids are asked for — it has no durable key, and neither an empty answer nor an answer keyed by the read ordinal is one (ADR-0140) |
| `IngestAsync` (`IDatasetIngest`) | `IngestRequest`, `FeatureBatch` pages | atomic create + load in one transaction; identity mode `Auto`/`Source`, so every ingested dataset is keyed (ADR-0149); additive face (ADR-0041). `sourceSrid` asserts the source CRS; a CRS the document declares itself wins over the caller's stamp, the decoded pages are reprojected through `ICoordinateTransforms` before load, and a contradiction between the two is `invalid.arguments` |
| `IngestStreamAsync` (`IDatasetIngestStream`) | `IngestRequest`, `FeatureSchema`, `IAsyncEnumerable<FeatureBatch>` | the same atomic load with pages arriving as they are decoded, so a large upload is never materialised; the schema is declared up front because the table is created before the first page (ADR-0041 §4) |
| `StartAsync` / `AppendAsync` / `DescribeAsync` / `ListAsync` / `OpenAsync` / `DiscardAsync` (`IUploadStaging`) | upload id, optional declared total + SHA-256, a chunk `Stream`, a byte offset | byte-level staging for a resumable upload (ADR-0090): a client appends chunks, reads back how many bytes landed, and names the staged upload in `POST /api/ingest?upload=`. Chunks are **bytes**, so the load stays the same one transaction; `Complete` is true only when a declared total is reached, and the staging is discarded only after the load commits. Additive optional face, implemented by the host |
| `ListAsync` / `GetAsync` / `PutAsync` / `DeleteAsync` (`IMapRegistry`) | map name / `Map` | runtime map registry (ADR-0053, evolving ADR-0041): declared entries immutable, runtime entries persisted; `Map` carries name, store, stable-id layers and the enabled `Services` (FeatureServer/MapServer/Tiles/Wms/Wfs/ImageServer); each layer may carry a persisted MapLibre style fragment (ADR-0047) and a `Kind` (feature/image) with an optional per-layer store |
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
- `Map`/`MapServiceKind`/`MapLayer`/`MapLayerKind` and the ingest records
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
dataset; a table without a primary key has no durable key to read by and the
read is refused with `invalid.arguments` rather than answered with nothing
(ADR-0140, which replaces ADR-0038 §2's empty result).

**Demo specifics:** read-only procedural datasets (110-point grid + 8
cities, EPSG:4326); bbox and attribute filtering via the in-process reference
evaluator (no SQL pushdown); writes/creation/editing rejected.

**Memory specifics (ADR-0042):** the ephemeral writable in-memory store
(keyed `memory`, always available) implements the writable faces including
`IDatasetIngest`, `IDatasetIngestStream`, `IFeatureEditStore` and
`IFeatureLookup`. State is
process-local and non-durable; every ingested (`Auto` or `Source`) dataset is
editable and lookup-able, because ADR-0149 removed the keyless ingest mode —
and a `CreateAsync`-built one is query-only and refuses a lookup, because it
declares no identity column (ADR-0147 keeps it keyless). A stored feature is keyed by the
dataset's identity column — the assigned id, or the source column's own value —
so a lookup by `OBJECTID` resolves on an `Auto`/`Source` dataset as it does on
a database's (ADR-0119); a dataset with no declared identity column is refused
the lookup rather than answered from whatever identity its rows carry
(ADR-0140). It is the **reference
evaluator** of the predicate vocabulary (ADR-0074 §4), so the SQL back ends
and the adapters are held to its answers by the shared conformance fixture.

**Map registry specifics (ADR-0053):** `Spatial.Maps`
composes immutable declared entries (config-seeded, whole-store entries
expand to the sorted dataset list with stable ids) with runtime entries in a
versioned JSON file written atomically, and reads a pre-ADR-0053 legacy map file
(`publications.json` wire shape) once for migration. `MapLayer` optionally carries
`Style`, a JSON array of MapLibre style-layer objects in the ADR-0044 subset,
persisted verbatim (ADR-0047); the dataset is not repeated inside it — the
host injects `source-layer` when it assembles a render document. The OGC
WMS/WFS projection lives in `Spatial.Adapter.Ogc`, reads the same map and
keyed stores, and adds no contract: it renders through `IMapRenderer` and
queries through `IFeatureStore`. A layer may also declare `Relationships`
(`LayerRelationship`, ADR-0077): two key columns plus a cardinality, with a
join dataset for many-to-many. The declaration is publication state, so it
lives on the layer and never in a store; `MapValidator` checks its shape and
`MapRelationshipSchemas` checks it against the live schemas where a
declaration happens (`PUT /api/maps/{name}`). The GeoServices projection
serves it as the layer's `relationships` metadata and as
`queryRelatedRecords`, with `relate`/`unrelate` behind the edit gate. An enabled
service must be fed by a layer of the matching `Kind`. `Auto`-identity
ingest adds a
`GENERATED BY DEFAULT AS IDENTITY` primary key; `Source` uses a named integer
field; those are the only two modes, so an ingested dataset always declares an
identity column and `identity=none` is refused by name (ADR-0149). Ingest declares each geometry column with the
data's coordinate layout (XY, XYZ, XYM or XYZM), so Z and M are preserved; a
column mixing layouts is `invalid.arguments`. `FeatureId.Unassigned` on an add
means the store assigns the key (ADR-0043).

**ArcGIS REST specifics:** read-only remote provider; writes, creation and
editing rejected.
