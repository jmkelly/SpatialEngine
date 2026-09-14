# Changelog

All notable changes to Spatial Engine are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

The product version is single-sourced in `Directory.Build.props`; update it and
this file together, then tag the release (`RELEASING.md`).

## [Unreleased]

### Added

- **WMS 1.1.1 capabilities dialect** (ADR-0053, T-045). `GetCapabilities`
  with `VERSION=1.1.x` serves the legacy dialect: DTD doctype, unqualified
  `WMS_Capabilities`, the SRS vocabulary and `LatLonBoundingBox` in lon/lat
  order with one `BoundingBox` per SRS. Absent or 1.3.x versions keep the
  1.3.0 dialect (what QGIS sends on add-layer); anything else is
  `InvalidParameterValue`. 1.1.1 `GetMap`/`GetFeatureInfo` KVP already
  worked; now 1.1.1 clients (GDAL, OWSLib) can negotiate from capabilities.
  `GetStyles`, `DescribeLayer` and `SLD`/`SLD_BODY` stay explicit
  `OperationNotSupported` rejects — the recorded client corpus carries no
  trace of them.

- **OGC failure diagnostics** (ADR-0045). The WMS/WFS adapter now logs one
  structured event per operation with the `request` operation and the merged
  request parameters, and logs a rejected operation at `Warning` with the
  mapped OGC `ServiceException` code, reason and HTTP status. A blank or
  400-rejected WMS layer in an interop client (for example QGIS) is now
  diagnosable from the log alone; previously only the path and status
  appeared.
- **Maps are the unit of authoring and exposure** (ADR-0053). `Map` replaces
  `Publication` across the engine: a named, ordered set of styled layers from
  one keyed store plus the set of services it exposes — `Feature`, `Map`,
  `Tiles`, `Wms`, `Wfs` and `Image`. A layer is owned by its map, so the same
  dataset can be styled differently in different maps. `IMapRegistry`
  (`Spatial.Provider.Maps`) persists maps to `maps.json` and reads a legacy
  `publications.json` once for migration; the GeoServices adapter serves only
  the services a map enables. New surfaces: map tiles at
  `GET /api/maps/{name}/tiles/{z}/{x}/{y}.{format}`, and OGC WMS 1.3.0 / WFS
  2.0.0 in the new `Spatial.Adapter.Ogc`. The neutral host API moves to
  `/api/maps` (deprecated `/api/publications` aliases remain for one
  release), and the workbench Composer becomes the Maps experience with
  service toggles and copyable endpoint URLs. The TypeScript and .NET SDKs,
  the seed tool and the OpenAPI snapshot move to the map contract (the CLI
  composes `Map` documents too).
- **COG and tiled GeoTIFF support** (ADR-0051, plan I4): the NetVips raster
  catalogue now reads a tiled/pyramidal GeoTIFF's structure (`tile-width`,
  `tile-height`, `n-subifds`) into `RasterInfo`'s block and pyramid fields,
  opens tiled rasters for random access, exports a downscale from the
  coarsest internal overview that still covers the output, and reports the
  ImageServer `minPixelSize`/`maxPixelSize` from the pyramid depth. A
  COG-style tiled+pyramidal file can be written through the concrete
  `VipsRasterCatalogue.WriteCogAsync` storage operation. No new dependency:
  libvips already covers the format, so the GDAL follow-up trigger is
  unchanged.
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
- **Spatial CLI** (ADR-0052): a dependency-free console client of the public
  host API at `clients/dotnet/Spatial.Cli`. It adds datasets through the
  neutral ingest route, composes styled maps (FeatureServer, MapServer or
  ImageServer), stores the workspace as a versioned declarative
  `spatial.json`, and reports each map's GeoServices endpoint. Descriptive
  long flags, a `--json` envelope, `--dry-run` and stable exit codes make it
  script- and LLM-friendly; it publishes as one self-contained binary.
  Quality-gated through `tests/unit/Spatial.Cli.Tests`.
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

- **Scale-aware WMS DPI** (ADR-0053, T-045). The QGIS `dpiMode=7` triple
  (`DPI`, then `MAP_RESOLUTION`, then `FORMAT_OPTIONS` dpi:N) now drives
  rendering instead of being accepted and ignored: `MapRenderRequest`
  gains a `Dpi` member (default 96, the CSS reference pixel style sizes are
  defined at) and the Skia pipeline scales paint sizes linearly with it
  while the output frame keeps the requested size. A malformed `DPI` or
  `MAP_RESOLUTION` is `InvalidParameterValue`; 96 dpi renders byte-identical
  to before, so existing QGIS traffic is unaffected.

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

- **QGIS Feature Service layer 400 ("unknown field 'OBJECTID'")**: the
  adapter's `orderByFields` validation resolved fields against the dataset
  schema only, but `OBJECTID` is synthetic (the identity column or scan
  ordinal, ADR-0037) and is the field clients order by for stable paging.
  `orderByFields=OBJECTID` now compiles to the resolved object id, and the
  `where` grammar resolves the same synthetic field (`EsriSyntheticField`),
  so `where=OBJECTID ...` works too — including `where`-based edits. The
  FeatureServer now renders in QGIS.
- **QGIS WMS returned "nothing to show" (HTTP 400)**: the WMS capabilities
  advertised each DCP `Get` URI with `?service=WMS&request=...` embedded.
  QGIS honours that URI and appends the operation parameters, so `service`
  and `request` arrived twice (`SERVICE=WMS,WMS`) and the adapter rejected
  the request. DCP endpoints are now the bare service URL (`...?`), and a
  parameter repeated with an identical value is collapsed, so an already
  added layer works without re-fetching capabilities.
- **WMS GetFeatureInfo missed the feature under the click**: the search was
  widened by only half a pixel, so clicking anywhere inside a rendered point
  marker (but not within half a pixel of its coordinate) returned an empty
  `FeatureCollection` and clients such as QGIS reported "no feature at the
  location". The tolerance now includes the layer's persisted marker radius
  (plus its stroke and the clicked pixel), so a click inside the drawn symbol
  identifies the point feature.
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
