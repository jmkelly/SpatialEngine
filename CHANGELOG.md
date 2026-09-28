# Changelog

All notable changes to Spatial Engine are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

The product version is single-sourced in `Directory.Build.props`; update it and
this file together, then tag the release (`RELEASING.md`).

## [Unreleased]

### Added

- **Query `distance`/`units` band, `returnCentroid` and `returnZ`/`returnM`**
  (ADR-0085, SpatialEngine-u2x.16): the three §7.1 query rejects that were
  really engine verbs are served. `distance` is a band from the query
  geometry applied as a buffer in the layer CRS, with the unit code resolved
  from the same curated table and projected/geographic rule the Geometry
  Service uses, so it composes with every exact `spatialRel`.
  `returnCentroid` is a new `IGeometryMeasures.Centroid` verb (the area
  centroid, not the envelope middle) written beside each feature's geometry.
  `returnZ`/`returnM` select the output ordinates.
- **The Feature Server layer advertises the Z/M its data actually carries**
  (ADR-0084, SpatialEngine-fhf): `hasZ`/`hasM` are on the layer resource, so a
  client can preflight whether a dataset carries elevations or measures before
  it asks for them. They are a description of the data, not a capability, and
  ArcGIS clients branch on them — a client told a 2D layer has Z will send Z
  in query geometry and edit payloads — so they are emitted only where the store
  proves the ordinate. `DatasetDescription` now carries the layout its store
  declares (`geometryLayout`, default `xy`), PostGIS reads it from the
  geometry column's declared type (`geometry(PointZ,4326)` → `xyz`), and a
  two-dimensional or unconstrained column advertises neither key rather than
  `false`. SQL Server (whose spatial types have no Z/M) and the ArcGIS REST
  provider report `xy` until they can prove more.
- **`findTransformations` is a search, and datum transformations are values**
  (ADR-0087, SpatialEngine-u2x.15, landed on ADR-0086): the operation used to
  be a reformulation of the catalogue — same datum `[]`, different datum one
  forward composite, `extentOfInterest` rejected *by name* because "the
  catalogue has no area-of-use model" — with the parameters welded to the
  adapter, so there was one possible answer and nothing outside the adapter
  could check it. A new `Spatial.Contracts.TransformationSearch` namespace
  carries `CrsTransformation`, `CrsTransformationStep`, `HelmertParameters`,
  `CrsAreaOfUse` and `CrsTransformationQuery` (records of strings, doubles
  and booleans, no packages), and `ICrsDirectory` gains `FindTransformations`.
  The graph is composed from the catalogue's own datum definitions, so it is a
  hub and spokes rather than a table of hand-written pairs: between two datums
  it offers the **direct** composed Helmert (the path the engine applies), the
  **concatenated** path through the WGS 84 pivot, and the same shift **reduced
  to three translations**, each carrying its steps, the seven EPSG-9606
  parameters it applies, its area of use and a stated accuracy. Accuracies
  combine in quadrature and the reduced form adds the first-order bound on what
  its dropped rotations cost (14.5 m measured against 41.9 m stated). Area of
  use follows the EPSG rule for the shape of the operation, so an empty
  intersection drops the direct candidate outright and `extentOfInterest`
  *filters* what is left instead of refusing — it is read in the source CRS's
  own coordinates and reprojected onto the geographic boxes the catalogue
  records. The search is symmetric: a reversed request returns the same
  operations with `transformForward: false`, and the default is every ranked
  candidate rather than one (`numOfResults` and the ArcGIS REST JS
  `numTransformations` both slice it). `project` now accepts a
  `datumTransformation` that names the operation it applies and refuses any
  other by naming that one; `vertical=false` is accepted and `vertical=true`
  stays refused. The published parameters are the engine's: a control point
  applies the returned Helmert to the London point by hand and lands within a
  millimetre of the PROJ 9 (OSTN15) reference. **Caller-visible changes:** a
  `findTransformations` response is a ranked array of candidates with
  `name`/`geoTransforms`/`accuracy`/`approximate`/`areaOfUse` (and `helmert` per
  step) instead of a one-element array, an unknown CRS is `invalid.arguments`
  rather than `[]`, and `project` no longer refuses `datumTransformation`
  outright.

- **The CRS catalogue's model is decided: WKT definitions, and datum quality
  data that is not part of a definition** (ADR-0086, SpatialEngine-u2x.28):
  the WKT path and the transformation graph were mutually incompatible because
  the graph read a datum's *accuracy* and *area of use* off the hand-written
  catalogue rows, which the WKT path deletes — and a WKT2 `GEOGCRS` genuinely
  carries neither, because both are attributes of the registered coordinate
  operation, not of the CRS. A definition therefore owns the datum's
  `TOWGS84` shift (a construction input ProjNet needs), and a new curated
  `EpsgDatumOperations` table beside the vendored WKT owns the accuracy in
  metres and the registered extent, joined to a definition by the datum's EPSG
  name. A datum with no published operation contributes no graph node rather
  than an invented accuracy, and a test fails if a geodetic definition is
  added to the WKT without a row. EPSG:3857 is unaffected: the pseudo-Mercator
  interception lives in the reader, and the graph builds no coordinate systems
  at all. The ADR also settles ADR numbering: the next free number on `main`,
  with `AdrNumberingTests` as the enforcement.

- **The Feature Server layer advertises the capability flags its query surface
  earns** (ADR-0081, SpatialEngine-u2x.25): the layer resource now carries
  `supportsQuantization` — at the top level, where the ArcGIS REST JS gate
  reads it, and inside `advancedQueryCapabilities`, where the S3 layer
  reference pins it — so a tiled web-map client finally sends the
  `quantizationParameters` the engine has served since ADR-0079 instead of
  seeing a false gate and asking for full precision. Alongside it,
  `advancedQueryCapabilities.supportsPaginationOnAggregatedQueries` is true,
  because an `outStatistics` response pages and reports
  `exceededTransferLimit` with a `resultPaginationToken`. The rest of the flag
  family stays absent rather than advertised `false`: each unserved flag
  (`supportsTrueCurve`, `supportsLod`, `supportsQueryWithDistance`,
  `supportsQueryWithDatumTransformation`, `supportsQueryAnalytic`,
  `supportsQuantizationEditMode`, `supportsValidateSQL`, `supportsCalculate`,
  …) names behaviour the facade rejects by name, and a key a client can read
  is a claim. A table — a dataset with no geometry field — omits the per-layer
  quantization flag, having no coordinates to quantize.

- **Deeper label placement: candidates, priority, line placement, font faces
  and a marker set** (ADR-0080, SpatialEngine-u2x.20): a label is no longer
  offered a single position. Each feature generates ordered candidates — a
  point offers its position and the four anchor offsets around it, and
  `symbol-placement: line` offers a candidate every `symbol-spacing` pixels
  along the projected line, aligned to the local direction, then the same
  candidates again in reverse — and the greedy first-fit takes the first that
  places anything, so a label that loses its first choice usually still draws.
  Which label wins is now a style priority (`symbol-sort-key`), decided in a
  placement pass of its own across all symbol layers — the order is
  `symbol-sort-key`, then the style's document order, then the existing
  identity/envelope-centre tie-break, which keeps it a total order — while the
  pixels still composite in document order. `symbol-allow-overlap` (inheriting
  `text-`/`icon-allow-overlap`) and `symbol-ignore-placement` bypass the
  collision test. `text-font` stops being a style error: the bundle grows to
  the four digest-pinned Noto Sans 2.003 faces, each name in the list resolves
  through a documented fallback chain (family and weight/style, else the
  nearest bundled weight, else the next name), and a family the engine has
  never heard of renders in the default face instead of failing the render.
  `text-transform`, `text-letter-spacing`, `text-line-height` and
  `text-rotate` are applied to the shaped string, and the sprite registry
  gains the circle, square, diamond, triangle and ring markers beside
  `default-marker`. Covered by the new placement, font and sprite suites and a
  second committed golden render (`symbols-line.png`); the existing
  `symbols.png` golden is byte-identical, because a style whose labels do not
  collide takes the same candidate and draws the same pixels it always did.

- **A deviation allowance is its own geometry verb** (ADR-0079,
  SpatialEngine-u2x.3): the Feature Service `query` now honours
  `maxAllowableOffset` instead of accepting it and returning full precision,
  and serves `quantizationParameters` instead of rejecting it by name. Both
  go through the new `IGeometryOperations.Generalize`, which states how far
  the answer may be from the true geometry rather than how coarsely the
  algorithm should thin it: every returned vertex is a vertex of the input,
  every input vertex stays within the allowance, and an allowance too wide to
  spend without changing a feature's geometry kind returns the input
  unchanged — so an offset of zero is byte-identical to full precision.
  `quantizationParameters` snaps x, y, z and m to the view grid anchored on
  the request's extent, then spends the rest of the budget on the same verb;
  an unservable `mode` or `originPosition` is still rejected by name.

- **A WKT definition path for the built-in CRS catalogue** (ADR-0027,
  SpatialEngine-u2x.17): every CRS the ProjNet provider serves is now
  defined as EPSG WKT and read by a reader of the engine's own, so the
  catalogue is no longer a hand-written parameter list. `ProjWkt` reads both
  the OGC WKT1 and the WKT2 dialect and hands what it reads to the one
  programmatic builder the catalogue already used — the UTM families are one
  WKT template with two substituted numbers, and the hand-written rows, the
  generated zones and the definitions that exist only as WKT are
  indistinguishable to callers. The Pseudo-Mercator workaround survives as
  code rather than as a refusal: the reader intercepts the identifiable
  spellings (the projection names, the CRS names, the EPSG
  3857/3785/900913/102100/102113 authorities) before construction and routes
  them to the known-good programmatic path, which matters because EPSG:3857
  is very widely published with the projection named `Mercator_1SP`, and
  ProjNet's own WKT reader reads that spelling as a plain Mercator —
  **33,931 m** too far south at Berlin's latitude, measured by test rather
  than asserted in a comment. Two definitions that are new to the served set
  come in with the path, both read as WKT and both verified against something
  other than the library that reads them: **EPSG:3395** (WGS 84 / World
  Mercator) against EPSG Guidance Note 7-2's method 9804, and **EPSG:2193**
  (NZGD2000 / New Zealand Transverse Mercator) against the projection's own
  analytics — its central meridian, scale factor, false easting, false
  northing and latitude of origin, and the meridian-arc series. **EPSG:3857
  and the other fourteen codes the catalogue already served are
  byte-identical**, pinned to the last bit, the catalogue is still built once
  and lazily, and the reader resolves only the projection methods it has
  been checked for, so a definition is never served with coordinates that are
  quietly wrong.

- **Ground-distance buffering** (ADR-0075, SpatialEngine-u2x.14): the
  GeoServices `buffer` operation now serves a linear `unit` against a
  geographic buffer CRS — the commonest request there is — through a new
  `IGeodesicBuffering` contract verb implemented as
  `ProjNetGeodesicBuffering`. It reprojects onto a local transverse Mercator
  sized by the work, buffers, and projects back: within **0.05% relative of
  the geodesic** for a working radius up to 300 km, and a typed
  `invalid.arguments` failure naming the radius and the remedy past that
  instead of a shape it cannot stand behind. `geodesic=true` is served on
  that path and refused by name against an angular unit, no unit or a
  projected `bufferSR`; `unionResults=true` dissolves the per-input buffers
  into one geometry. The projected-`bufferSR` planar path is unchanged and
  remains the exact answer inside a valid zone.
- **UTM zone families in the built-in CRS catalogue** (ADR-0027,
  SpatialEngine-u2x.6): the catalogue now *generates* the projected families
  it used to enumerate by hand — the UTM grid over all sixty zones in both
  hemispheres (EPSG 32601-32660 and 32701-32760) plus the ETRS89 and NAD83
  UTM bands over their own datums — from one Transverse Mercator parameter
  template. `Describe` and `Transform` therefore succeed for any UTM zone
  instead of failing with `invalid.arguments` outside the seven zones that
  were typed out. A generated zone is byte-identical to the hand-written row
  it replaces (the served coordinates of all fifteen pre-existing codes are
  pinned to the last bit), the catalogue is still built once with each CRS
  built on first use, and the ProjNet Pseudo-Mercator workaround is
  untouched.
- **Relationships are declared, traversed and written**
  (ADR-0077, SpatialEngine-u2x.22): a map layer now declares how its records
  relate to another of the map's layers over two key columns
  (`LayerRelationship`: one-to-one, one-to-many, or many-to-many through a
  join dataset), validated structurally when the map is stored and against
  the live schemas where the declaration happens. The declaring layer
  advertises the relationship in its `relationships` metadata, the Feature
  Service serves `queryRelatedRecords` over it (the related layer's own
  `where`, `outFields`, `geometry`/`spatialRel`, `time` and `outSR` all
  apply), and `relate`/`unrelate` move the same key behind the existing
  admin-token edit gate, one result per origin/related pair. The
  `geoservices-compatibility.md` §7.1 non-goal that listed
  `queryRelatedRecords` (and, wrongly, attachments) as absent is now
  corrected.


- **SQL Server store provider** (ADR-0073, T-113): `Spatial.Stores.SqlServer`
  implements the catalogue, feature, lookup, transaction, editing, ingest and
  attachment faces on Microsoft.Data.SqlClient, wired into the host under the
  store key `sqlserver` (`Spatial:SqlServer:ConnectionString` or
  `SPATIAL_SQLSERVER_CONNECTION`). Geometry crosses as OGC WKB, a dataset's CRS
  is discovered from its data and then from a provider-owned `spatial_datasets`
  sidecar, only XY geometries are written (Z/M is refused, not flattened), and
  the store-backed matrix is proven against a real SQL Server container with
  Testcontainers alongside a DB-free unit suite.

### Changed

- **The Esri geometry writer states its `hasZ`/`hasM` flags**
  (ADR-0085, SpatialEngine-u2x.16): a three-ordinate Esri coordinate array is
  Z when only `hasZ` is set and M when only `hasM` is set — the codec's own
  read rule — so a 3D geometry was being written in a form the same codec
  would read back with the wrong ordinate. Checked as the bead asked: the
  canonical binary codec round-trips `Xyzm` exactly, so the loss was Esri
  codec depth, not the engine model.

- **Geometry Service `simplify` is generalization again** (ADR-0036, §7.0.5):
  it now calls `IGeometryOperations.Simplify` (Douglas-Peucker) with the
  tolerance the request carries — `deviation`, or mutually exclusively
  `value` — the same engine verb `generalize` already used under its
  `maxDeviation` name. It used to call `IGeometryProcessing.Repair`
  (topological MakeValid) and ignore both parameters, so a valid geometry
  came back whole and a self-intersecting one was silently repaired instead
  of thinned. A request with neither tolerance, with both, or with a
  negative one is now a named `invalid.arguments` failure. Topological
  repair keeps its engine verb and its home: no Esri operation names it, so
  nothing in the facade maps to it.

- **Feature Service `spatialRel` is exact DE-9IM, not envelope arithmetic**
  (ADR-0036, SpatialEngine-u2x.2): `Contains`, `Within`, `Touches`,
  `Overlaps` and `Crosses` on the Feature/Map query and Image Service
  catalog paths are now intersection patterns over
  `IGeometryRelations.Relate`, with the envelope tests kept as the
  pre-filter. The interior results are unchanged; the boundary cases the
  approximation documented as approximate now follow OGC semantics, so three
  answers change: a containee lying *on* the container's boundary is no
  longer `Contains`, a point or line on a feature's boundary is now
  `Touches`, and a line crossing a feature is no longer `Touches`.
  `Intersects` stays the non-empty intersection and
  `esriSpatialRelEnvelopeIntersects` stays the envelope test. The Geometry
  Service `relation` operation, which already mapped the same verbs to the
  same patterns, is unchanged.

### Fixed

- **A write now invalidates the tiles derived from it** (ADR-0083,
  SpatialEngine-u2x.21): the tile cache key fingerprinted the *request*, so
  nothing in it moved when a feature was written, edited or ingested, and
  every cached tile of an edited map stayed stale until someone called
  `DELETE /api/render/cache` by hand. A store may now report a per-dataset
  content version (`IVersionedFeatureStore`), the in-memory store bumps it on
  every write, edit and ingest, and every tile key — the neutral raster single
  and batch routes, the map raster and MVT routes, and the Esri
  `MapServer/tile` and `MapServer/vectorTile` routes — folds it in. Tiles
  report the version they were rendered at in `X-Tile-Version`, which
  `SpatialClient.Tiles.RenderWithVersionAsync` returns. A store that reports no
  version (PostGIS, SQL Server, demo, ArcGIS REST) behaves exactly as before.
- **Fused-cache MapServer root advertises the tile scheme reference**
  (ADR-0048): a tiled MapServer root now serves its spatial reference,
  `initialExtent`/`fullExtent` and units in the tiling SR (layer extents
  reprojected server-side, Web-Mercator inputs clamped to validity) instead
  of the data CRS. Advertising 4326 extents alongside a 3857 `tileInfo`
  made QGIS derive Null-Island tile indices for a real canvas — every tile
  request 200, every tile blank. Untiled roots still advertise the data CRS.
- **MapServer tile/root diagnostic logging**: one `Debug` event per tile
  resolves the requested address to its rendered geography and scheme, and
  the root logs its advertised SR/extents versus the tile-scheme SR, so a
  client fetching the wrong tiles is distinguishable from the server
  rendering the wrong geography.


## [0.3.0] - 2026-09-15

### Added

- **Feature attachments end to end** (ADR-0065/0066, T-060/T-061/T-088):
  the additive `IFeatureAttachmentStore` SDK capability with provider-owned
  bytes, served `hasAttachments`/`queryAttachments`/add/update/delete plus
  the single-attachment bytes resource (the OpenAPI snapshot gains
  `.../attachments/{attachmentId}` GET), with PostGIS sidecar persistence.
- **Guid identity columns** (ADR-0067, T-058): `uniqueIds` for guid-identity
  columns are served in canonical form.
- **ImageServer authored metadata XML** (ADR-0068, T-056): the authored
  ISO/FGDC document is served verbatim as `application/xml` at both the
  service level and the per-item level.
- **Esri compatibility projections** (ADR-0054–0059, T-071): ImageServer
  legend/find/statistics/histograms/attribute-table/thumbnail/metadata,
  MapServer legend/queryDomains/queryLegends/generateRenderer, modern
  Feature query params, percentile statistics with capability-flag honesty,
  Map export time/dynamicLayers/layerOption/cached-root honesty, and
  Image capability flags with named rejects; offline/async surface stays
  rejected by name (ADR-0060). Proven by the Esri-docs replay suite D.
- **Feature write-model extensions** (ADR-0061): service query,
  generateRenderer reuse, validateSQL, aggregation honesty, attachments.
- **Workbench**: service Parity page (T-072), Geometry playground + Image
  tab (T-073), Chaos simulation toggles proving typed error mapping
  (T-074).
- **Performance suites**: suite C throughput smoke and suite D full-job
  baselines with a nightly job (T-077/T-078/T-086), OpenLayers proof at
  vectorCount 12 (T-090).
- **Interop test infrastructure**: live Esri fixture refresh script
  (T-065) with the GeometryServer root fixture drift pinned (T-093).
- **Documented non-goals**: vector tiles and OGC API Tiles (ADR-0062);
  BenchmarkDotNet for the benchmark suite (ADR-0063).

### Changed

- **Quality loop cleared to green again** (ADR-0069): high-complexity
  methods split into cohesive engines (`FeatureStatisticsEngine`,
  `FeatureSpatialMatcher`, `FeatureProjection`, `FeatureResponseWriter`,
  `FeatureQueryHandlers`, `FeatureAttachmentHandlers`,
  `GeoServicesResolution`, `ImageFileHandlers`, `ImageLegendBuilder`,
  `WmsGmlWriter`) with real unit tests; CRAP 0 of ~2600 methods,
  authored branch coverage 83.2% (floor 70%), Core.Tests mutation 91.2%
  (break 80). The two namespace `architectural-rigidity` diagnoses are
  advisory (shared-codec coupling prescribed by ADR-0035; the tool offers
  no per-diagnosis lever), so `failOn.severity` returns to `high`, and the
  structural architecture suite is waived out of the mutation gate.
- **Esri admin projection deltas aligned** (T-062): uploads MaxFeatures
  cap plus publish merge semantics; demo catalogue summaries cached
  (T-096); WorldCities snapshot loads lazily (T-095); CRS lookups cached
  on the transform hot path (T-087); query burst-tail diagnosed with Esri
  writer bytes sent direct (T-092).
- **Deterministic legend swatch URLs** (T-085).
- **ADR-0059 item 5 amended** (T-082): `hasHistograms` for every
  real-valued band format.

### Fixed

- **Malformed multipart ingest** (T-094): rejected as a typed 400 naming
  the file part.
- **WFS GetFeature ids** (ADR-0064, T-084): ids are scoped per typeName,
  so unique ids hold across layers.

## [0.2.0] - 2026-09-14

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

- **Deprecated `/api/publications` aliases** (ADR-0053 §4, T-079): the
  pre-0.2.0 aliases (`GET /api/publications[/{name}]`,
  `PUT`/`DELETE /api/publications/{name}`,
  `POST /api/publications/{name}/render`) are gone and answer 404;
  `/api/maps[/{name}]` and `/api/maps/{name}/render` are canonical. The
  `publications.json` → `maps.json` migration shim is unchanged.
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
