# Composition: Interfaces, Implementations, Lifecycle (distilled)

Implements ADR-0033 (replaces ADR-0006/0013/0025 worker boundaries).

<!-- orientation:begin -->

## Orientation

One line per closed bead: where the first hour went (`SpatialEngine-rzq`).

- Hold the discovered description per store, keyed by dataset, and treat the
  write methods as the invalidation set: a walk of N pages costs one discovery,
  and every write the store makes forgets what it could have changed.
  (SpatialEngine-u2x.41)
- State the byte order on every pushed string term (`COLLATE "C"` on Postgres,
  `Latin1_General_100_BIN2` on SQL Server): the database's own collation is a
  locale, and an all-lower-case fixture cannot see the difference.
  (SpatialEngine-u2x.43)
- Compile a pushed text predicate through the same `Ordered` helper the sort
  keys use, so a `WHERE` and an `ORDER BY` cannot disagree about the order.
  (SpatialEngine-u2x.48)
- Start the SQL Server suite's container once for the assembly (a collection
  fixture, a database per class) and read the skip reasons: a run that reports
  itself mostly skipped is twelve container starts losing a 60-second wait
  strategy, not a missing daemon. (SpatialEngine-qhz)
- Declare the order on the sidecar columns the store itself owns; a
  case-folding key cannot hold both codes at all, so restating the collation
  per statement cannot fix it. (SpatialEngine-u2x.57)
- Name a pushed row by the identity columns' positions in what was read, not by
  the columns appended for the mapper: the appended set named every pushed row
  of a keyed table by its ordinal. (SpatialEngine-u2x.55)
- Ask what a declined pushdown is protecting before making every face decline
  it: ADR-0097's rule is about the ordinal a feature is named by, and a count,
  a distinct set and a grouped reduction return values, so they push on a
  keyless layer where the feature read cannot. (SpatialEngine-xg5)
- Measure a store reduction through the store's reduction face
  (`IFeatureAggregateStore`, probed as `FeatureReductionFallback` does), not a
  `QueryAsync` plan read: the spike's `B`/`Bp` cells reduced a read in the
  adapter and so measured the read, not the face ADR-0184 pushes.
  (SpatialEngine-8dm)
- Measure where an allocation is before naming the class that owns the feature:
  the 15 MB was two SQL stores' row mapping building a second feature per row,
  not `FeaturePlanExecutor` — decide the projection once per read, and order a
  page's window rather than the read. (SpatialEngine-yup)
- Apply each sort key as a then-key in `FeaturePlanExecutor.Order`; a fresh
  `OrderBy` per key made a composite order its last key's, and the SQL
  pushdowns were narrowed to the orders both sides agreed on.
  (SpatialEngine-u2x.54)
- Read CRS definitions through `ProjWkt`, our own WKT reader: ProjNet's path
  lost 33,931 m of northing on the widely published `Mercator_1SP` spelling of
  EPSG:3857. (SpatialEngine-u2x.28)
- Check ProjNet for the projection's convention before writing its arithmetic:
  `Hotine_Oblique_Mercator` and `Oblique_Mercator` are one oblique Mercator
  differing only in where the false offsets go, so variant A needed a map entry.
  (SpatialEngine-r4o)
- Key every ingested dataset (`Auto` or `Source`) and refuse `identity=none` by
  name on both ingest routes and the CLI; `CreateAsync` stays keyless on
  purpose, and each path says why they differ. (SpatialEngine-2cm)
- Read the grid exercise's environment rather than assuming it: a bundle under
  `SPATIALENGINE_GRID_DIR` and a `cs2cs` that opened it are two separate facts,
  and PROJ answers from a Helmert when the grid is missing, so a run printing
  neither is green for the wrong reason. (SpatialEngine-yt2)
- Write a grid fixture in the container's convention, not the engine's: a NADCON
  pair states every longitude positive west, so an east-positive fixture pins a
  reader against a file NADCON does not publish, and the sign defect it hid is
  one no rule catches. (SpatialEngine-90n)
<!-- orientation:end -->

## Composition model

Implementations are linked .NET projects composed by `Spatial.Host` with
Microsoft DI — keyed services where two stores serve one contract:

- `IDataCatalogue`/`IFeatureStore` keyed `"demo"` (`DemoStore`, always
  available), `"memory"` (`MemoryStore`, ADR-0042), `"postgis"`
  (`PostgisStore`, ADR-0010/0028), `"sqlserver"` (`SqlServerStore`,
  ADR-0073) — each database store needs a connection string — and one key per
  configured ArcGIS REST service (`ArcGisRestStore`, ADR-0035).
- `IFeatureEditStore` keyed `"memory"`, `"postgis"` and `"sqlserver"`
  (ADR-0037/0072); the demo and ArcGIS REST stores are read-only and do not
  implement it.
- `IFeatureLookup` keyed `"memory"`, `"postgis"` and `"sqlserver"`
  (ADR-0038/0072); the demo and ArcGIS REST stores do not implement it and
  callers fall back to the scan. A dataset that declares no identity column is
  refused with `invalid.arguments` naming the dataset, not answered with an
  empty result and not answered from the scan's ordinal (ADR-0140).
- `IFeatureAggregateStore` is the reduction face of the query plan
  (ADR-0074): keyed by whichever stores implement it, absent for the rest,
  and callers compute the reduction over the returned page. Not yet
  implemented.
- `ITransactionStore` keyed `"memory"`, `"postgis"` and `"sqlserver"`; the
  demo and ArcGIS stores are read-only.
- `IDatasetIngest` and `IFeatureAttachmentStore` keyed `"memory"`,
  `"postgis"` and `"sqlserver"` (ADR-0041/0065); the SQL Server provider owns
  the same `spatial_attachments` sidecar shape as PostGIS. Both providers'
  sidecar declares its text `dataset`/`feature_id` key columns under a
  byte-order collation (`COLLATE "C"` / `Latin1_General_100_BIN2`) and
  re-collates a sidecar an earlier version created, because the sidecar is the
  store's own table — the one place the declaration, rather than the per-row
  term, is the way to say what an identity is compared by (ADR-0130).
- A PostGIS column that declares a collation of its own is discovered with the
  schema (`information_schema.columns.collation_name`) and held with it, so a
  pushed-down string comparison is written under the collation *that column*
  carries and not the database's — an authored `COLLATE "de-x-icu"` column on
  a `C` database still states `COLLATE "C"`, and an authored `COLLATE "C"`
  column on a locale database takes no term (ADR-0136).
- `IGeometryOperations`, `IGeometryMeasures`, `IGeometryProcessing`,
  `IGeometryRelations`, `ICrsDirectory`, `ICoordinateTransforms`,
  `IDemoWork`, `IMapRenderer`, `IRasterOperations`, `ITileScheme` and
  `ITileCache` as singletons (ADR-0044/ADR-0046). `ITileScheme` and
  `ITileCache` are the pluggable tiling seams: new projections/cache owners
  are additional registrations, not host changes. `IRasterCatalogue` is
  keyed `"raster"` (ADR-0051), configured from `Spatial:Raster` and
  implemented by `Spatial.Imagery.Vips`.
- `Spatial.Adapter.GeoServices` is mounted by the host at
  `Spatial:GeoServices:Root` (ADR-0035).
- `IStoreRegistry` (`Spatial.Host`'s `KeyedStoreRegistry`) is the single
  typed seam over the keyed stores: a request's store name resolves to its
  catalogue, feature store and additive faces, so the boundary adapters and
  the host API depend on the registry and never hold `IServiceProvider`
  (ADR-0033). The container is captured only in this one implementation,
  where the runtime key is needed.

In order of preference for new implementations:

1. **In-process implementation** (default): a project referencing Core +
   SDK only, registered in `Program.cs`.
2. **External service**: only behind a new narrow interface with an ADR.

## Project rules

- `Spatial.Core` — values only, zero packages, zero references.
- `Spatial.Contracts` — interfaces + DTOs over core types only, zero
  packages, only a Core reference.
- `Spatial.Esri.Codec` — the shared Esri wire codec: Core only, no NTS,
  ASP.NET or HttpClient (ADR-0035).
- `Spatial.Ingest.Codec` — upload format decoders to canonical batches, the
  streaming decode and the decode report: Core only, no ASP.NET or
  HttpClient, and no reference to `Spatial.Contracts` — it reaches a
  coordinate transform through its own `IIngestReprojection` seam, which the
  host adapts (ADR-0041, ADR-0082).
- Implementations (`Spatial.Operations.*`, `Spatial.Transformations.*`,
  `Spatial.Stores.*`, `Spatial.Maps`, `Spatial.Rendering.*`, `Spatial.Imagery.*`,
  `Spatial.Tiling.*`) — reference Core + SDK only; third-party packages
  (NTS, ProjNET, Npgsql, SkiaSharp, NetVips) stay inside the owning
  implementation (ADR-0005).
- Boundary projects (`Spatial.Adapter.GeoServices`,
  `Spatial.Stores.ArcGisRest`) additionally reference only the codecs
  (`Spatial.Esri.Codec`, plus `Spatial.Ingest.Codec` for the serving adapter); the serving adapter owns ASP.NET Core, the consuming client owns
  `HttpClient` (ADR-0035).
- `Spatial.Host` — the only project that references implementations.

## Lifecycle

Processes are the host's own: start, serve, stop. There are no worker
processes, no health-check/restart supervision, no side-by-side versions,
no drain/rollback. Restart-to-upgrade is the accepted replacement story.
Plugin code is fully trusted; persistent state is external (the database).
