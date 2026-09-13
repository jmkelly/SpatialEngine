# Changelog

All notable changes to Spatial Engine are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

The product version is single-sourced in `Directory.Build.props`; update it and
this file together, then tag the release (`RELEASING.md`).

## [Unreleased]

### Added

- **ImageServer catalog operations** (ADR-0051, plan I3): the GeoServices
  ImageServer now serves the full catalog `query` (the Feature Service safe
  `where` subset, `objectIds`, geometry, `outFields`, `orderByFields`,
  paging, ids/count/extent/distinct and `outSR`), the §8.2 Raster Image and
  §8.3 Thumbnail resources, and the §8.0.7 Download Rasters / §8.5 Raster
  File surface. `IRasterCatalogue` gains `ListFilesAsync`/`ReadFileAsync`
  over opaque provider-owned file ids (paths never cross); raw download is
  opt-in (`Spatial:GeoServices:AllowRasterDownload`), size/file-capped and
  range-capable. `Spatial:Raster` can now declare catalog attributes and
  items, so catalogs are configurable end-to-end rather than test-only.
- **Map labels and sprite symbols** (ADR-0049): the Skia
  renderer's MapLibre subset gains `symbol` layers — `SkiaSharp.HarfBuzz`
  text shaping, a deterministic label placement/collision pass, and
  `Svg.Skia` sprite icons. Fonts are an embedded, pinned `NotoSans` resource
  (no system-font dependence) and the default marker sprite is embedded
  (ADR-0049). Unsupported symbol properties stay typed `invalid.arguments`.
- **ImageServer projection** (ADR-0051): a `PublicationKind.Image`
  publication is served as an ArcGIS ImageServer — root metadata (extent,
  pixel size, band count, pixel type, service data type, catalog
  `fields`/`objectIdField`), raster info, catalog item/listing, `identify` and
  `exportImage` (`f=image` bytes or JSON `href`, bbox/image SR, png/jpg/tiff,
  interpolation, compression, `pixelType`, `noData`). The raster boundary is
  provider-owned: `Spatial.PluginSdk` gains the core-typed `IRasterCatalogue`
  contract (no raster values, no third-party types), `Spatial.Imagery.Vips`
  implements it over the managed NetVips path behind `Spatial:Raster`
  configuration, and only encoded image bytes, core metadata and core
  geometry footprints cross the contract. GDAL is a measured-demand
  follow-up; raster analytics/functions remain non-goals. Architecture tests
  pin the SDK's package-free, core-typed surface.
- **Persisted per-layer style on publications** (ADR-0047):
  `PublicationLayer` gains an optional MapLibre style fragment (`string?`),
  validated as a JSON array of style-layer objects and persisted in
  `publications.json`. The workbench composer writes it on publish and reads
  it back on load, and `POST /api/publications/{name}/render` renders a
  publication server-side.
- **MapServer projection** (ADR-0048): a `PublicationKind.Map` publication is
  served as an ArcGIS MapServer — root, layer, `query`, `identify`, `find` and
  render — with the persisted style lowered to `drawingInfo`; covered by
  ArcGIS REST JS end-to-end tests.
- **Rich MapServer style metadata** (ADR-0050): the adapter projects the
  persisted MapLibre fragment onto `uniqueValue` and `classBreaks` renderers
  (equal-value and interval `filter` siblings), the single-field `esriTS`
  `labelingInfo` subset, and coded-value/range `domains` for the rendered
  field; the §4.7 image resource is mounted and returns a typed `not.found`
  (the engine has no picture symbols). All Esri types stay inside
  `Spatial.Adapter.GeoServices`; the ArcGIS REST JS e2e reads a rich layer.
- **Engine-side ingest reprojection**: `POST /api/ingest` accepts a
  `sourceSrid` and transforms every decoded page to the target SRID through
  the ProjNet `ICoordinateTransforms` service, so uploaded data in a curated
  CRS lands in a declared column CRS. The .NET and TypeScript SDKs pass the
  optional parameter through.
- **Seed tooling** (`eng/seed.sh`, `tools/seed/`): an on-demand script that
  fetches real public data (Natural Earth, USGS), ingests it — including the
  4326 → 3857 reprojection — and publishes a set of styled feature and map
  services through the neutral admin API, idempotently.
- **Structured logging to Seq** (ADR-0045): `Spatial.Host` logs through
  Serilog — console always, Seq when `Spatial:Logging:Seq:Url`
  (`SPATIAL_SEQ_URL`) is set — with one structured event per request
  (`UseSerilogRequestLogging`, server errors at `Warning`), a startup
  summary and actionable configuration warnings. The Aspire AppHost runs a
  Seq container (`Aspire.Hosting.Seq`) and injects its endpoint; the host
  still runs with no Seq (ADR-0018). Diagnostics carry configuration
  *state* only, never a connection string or token.
- **Map composer**: a workbench
  screen that composes engine datasets into an ordered, styled MapLibre
  preview — add catalogue datasets or upload GeoJSON/NDJSON/CSV inline,
  reorder layers by drag and drop, style them, and publish the composition
  as a neutral feature or map service through the existing
  `PUT /api/publications/{name}` and `POST /api/ingest` routes. Loads and
  deletes existing services, preserving their stable layer ids. Per-layer
  style is persisted with the publication as a MapLibre fragment (ADR-0047);
  the composer round-trips it on publish and load.
- **Tiles and a pluggable tile cache** (ADR-0046): the core-typed
  `ITileScheme`/`ITileCache` contracts in `Spatial.PluginSdk`
  (`TileCoordinate`, `TileLevel`, `TileCacheKey`) with the
  `Spatial.Tiling.WebMercator` (EPSG:3857 XYZ) scheme as the first
  implementation, a host-local in-memory LRU `ITileCache` (configurable byte
  and entry bounds), and `TileService` (cache-aware single tile plus an
  ordered, bounded-parallel batch). The host serves
  `POST /api/render/tiles/{z}/{x}/{y}.{format}`,
  `POST /api/render/tiles/batch`, `GET /api/render/tiles/capabilities` and
  `DELETE /api/render/cache`; the .NET client exposes
  `SpatialClient.Tiles.RenderAsync`/`CapabilitiesAsync` and the TypeScript
  client `renderTile`/`renderTiles`/`tileCapabilities`.
- **Raster rendering pipeline** (ADR-0044): the core-typed
  `IMapRenderer`/`IRasterOperations` contracts and DTOs in
  `Spatial.PluginSdk`, the `Spatial.Rendering.Skia` vector rasterizer
  (MapLibre-subset `background`/`fill`/`line`/`circle`, attribute filters,
  zoom windows, bbox pushdown, screen-space simplify/cull) and the
  `Spatial.Imagery.Vips` NetVips imagery pipeline
  (read/normalise/compose/encode). The host serves `POST /api/render` and
  `GET /api/render/capabilities`, configured by `Spatial:Rendering` and
  `Spatial:Imagery`; the .NET and TypeScript clients expose `RenderAsync` /
  `render`.
- **Ingest codec** (ADR-0041): `Spatial.Interop.Ingest` decodes GeoJSON,
  newline-delimited GeoJSON and CSV uploads into canonical `FeatureBatch`
  pages with inferred schemas (`DatasetDecoder.Decode`). Core-only; no host
  wiring yet.
- **Ingest and publication SDK contracts** (ADR-0041):
  `IPublicationRegistry` with the core-typed
  `Publication`/`PublicationKind`/`PublicationLayer` runtime service registry,
  and the additive `IDatasetIngest`
  (`IngestRequest`/`IngestOutcome`/`IngestIdentity`) atomic bulk
  create-and-load capability. Contracts only — implementations, the host
  admin API and the Esri admin projection land in later phases (ADR-0041).

### Changed

- **Quality loop cleared to green** (ADR-0040): the raster/MapServer/
  ImageServer work plus pre-existing baseline debt were paid down in one
  pass — high-complexity methods split into cohesive services
  (`MapStyleProjection`, `ImageService`, `VipsRasterCatalogue`,
  `MapRenderEngine`, the GeoServices endpoint groups and `VipsEncoder`),
  `Program.cs` de-top-levelled off the CRAP `Program.<Main>$` entry, and
  real unit tests added. `crap4dotnet` CRAP, authored branch coverage,
  `.dependably` metrics and the warnings gate are all green.
- **Viewport bbox pushdown direction fixed** (ADR-0046):
  `GeometryPipeline.TransformEnvelope` transformed viewport bounds the wrong
  way (`dataset CRS → viewport CRS` instead of `viewport CRS → dataset CRS`),
  which only surfaced for a world-covering Web-Mercator tile against a
  geographic dataset. The direction is corrected and pinned by a unit test.
- **Quality metrics gate recalibrated** (ADR-0040): `.dependably` now uses
  published thresholds (cyclomatic ≤ 15, cognitive ≤ 15, nesting ≤ 4, MI
  ≥ 20, in-repo coupling ≤ 40), disables the raw LCOM4 rule (it is
  meaningless for stateless types and gates through the tool's guard-aware
  diagnoses) and sets `failOn` to `moderate`. The metrics gate drops from
  26 high findings to 3 high + 1 moderate, all genuine.
- **Research code excluded from the metrics gate**: `.dependably` ignores
  `**/research/**`, so research probes are never measured as product code.

### Removed

- **Rendering research spike** (`research/rendering/spike/RenderSpike`): the
  throwaway vertical slice is deleted now that its findings are promoted into
  ADR-0044 and the production `Spatial.Rendering.Skia` /
  `Spatial.Imagery.Vips` projects. Its measured results remain recorded in
  `research/rendering/README.md`. Because `crap4dotnet` globs every `.csproj`
  under the solution directory (ignoring solution membership), the spike had
  produced 21 of the 22 CRAP gate findings; only the known
  `PostgisEwkb.WritePoint` coverage-matching artifact remains (see HANDOFF.md).

### Fixed

- **Composer crashed when served over plain HTTP**: adding an existing
  service (or any composer layer) called `crypto.randomUUID`, which browsers
  only expose in secure contexts, so on a LAN HTTP origin it threw
  `crypto.randomUUID is not a function`. Identifiers now go through a shared
  `newId` helper that falls back to `crypto.getRandomValues` when the native
  helper is absent.

## [0.1.0] - 2026-09-12

First tagged release: the independently executable .NET 10 host, the browser
React + MapLibre workbench, both SDKs, the PostGIS store, and the Esri
GeoServices REST serve/consume/edit boundaries.

### Added

- **Core and contracts** — `Spatial.Core` spatial value model (coordinates,
  geometries, CRS identity, features, SGEOM/SFBAT codecs) with no
  dependencies; `Spatial.PluginSdk` interfaces over core types only.
- **In-process services** (ADR-0033) composed by DI in `Spatial.Host`:
  geometry operations on NetTopologySuite (ADR-0005/0036), CRS description and
  transformation on ProjNet, the Docker-free demo store, and the PostGIS store
  (catalogue, dataset, scan/query/write, transactions, editing).
- **Host HTTP API** — typed routes (`/api/geometry/*`, `/api/crs/describe`,
  `/api/coordinates/transform`, `/api/catalogue`, `/api/datasets`,
  `/api/features/*`, `/api/transactions/*`, `/api/demo/sleep`), structured
  `SpatialException` codes, health endpoints and an OpenAPI document.
- **SDKs** — `clients/typescript` (`@spatial/client`, generated wire types with
  drift checking) and `clients/dotnet/Spatial.Client`.
- **Browser workbench** — React 19 + TypeScript + MapLibre served by the host
  from `Spatial:WebRoot`; catalogue, map rendering and selection, typed
  operations, result preview with browser-side persistence and cancellation.
- **Esri GeoServices REST boundary** (ADR-0035/0036/0037/0038) — serving
  (`Spatial.Adapter.GeoServices`: catalog, Geometry Service, FeatureServer
  query and gated editing) and consuming (`Spatial.Provider.ArcGisRest`), on
  the shared `Spatial.Interop.Esri` codec and filter grammar. The compatibility
  claim is proven against the official ArcGIS REST JS client.
- **Quality gates and delivery** — `eng/verify.sh` (format · build · tests),
  the two end-to-end scripts, `eng/*` build helpers, the container image
  (`Dockerfile`), and the CI workflow.
