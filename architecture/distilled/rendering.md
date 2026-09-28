# Raster Rendering

The condensed form of ADR-0044 (and ADR-0049/ADR-0080 for labels/symbols,
ADR-0076 for the expression dialect): turn the
engine's vector outputs into styled raster output over a NetVips imagery
pipeline. Read the
ADR for the decision and the research at
`../../research/rendering/README.md` for the measured baseline and the
libvips traps; this is the as-built shape.

## Contract surface (`Spatial.Contracts`)

Core/framework types only — no Skia, no NetVips (ADR-0005). The value types
sit in the root `Spatial.Contracts` namespace; the wire DTOs in
`Spatial.Contracts.Http`.

| Type | Role |
| --- | --- |
| `IMapRenderer.RenderAsync(MapRenderRequest)` | the whole pipeline → `RasterImage` |
| `IRasterOperations.ReadAsync(RasterReadRequest)` | load/normalise one configured imagery source |
| `IRasterOperations.CompositeAsync(RasterCompositeRequest)` | blend a bottom-to-top layer stack, encode |
| `IRasterCatalogue` (ADR-0051) | raster dataset metadata (block/pyramid structure included), catalog items/query/identify, warped/encoded export (overview-aware) and raw file listing/reading for the ImageServer |
| `RasterViewport(Envelope Bounds, int Width, int Height, string Crs)` | the viewport, x-first |
| `RasterBuffer` / `RasterImage` | raw (premultiplied RGBA by default) pixels / encoded bytes |
| `RasterLayer`, `RasterBufferLayer`, `RasterSourceLayer` | the composite stack entries |
| `MapLayerSource(Dataset, Features, Catalogue, Filter, Time)` | a resolved keyed store + catalogue, with an optional `MapTimeExtent` render filter (ADR-0058) |
| `RasterFormat`, `RasterPixelFormat`, `RasterBlend` | png/jpeg/webp/tiff, rgba8888/rgb888, blend modes |
| `ITileScheme` + `TileCoordinate`/`TileLevel` | pluggable tiling: address → projected extent + LODs (ADR-0046) |
| `ITileCache` + `TileCacheKey` | content-addressed tile cache for raster and MVT bytes; ownership is the implementation's (ADR-0046/0070) |
| `IVectorTileService` + `VectorTileRequest`/`VectorTileLayer`/`VectorTile` | core-typed MVT feature-layer request and encoded result; no protobuf types cross the contract (ADR-0070) |

The renderer receives **resolved** services in the request (no DI/service
location), so it is unit-testable with fakes; `Spatial.Host` resolves each
layer's keyed store and catalogue at the edge.

## Vocabulary

- **Raster** = contract types (`RasterImage`, `RasterViewport`, `RasterInfo`, …).
- **Render / Skia** = vector drawing (`Spatial.Rendering.Skia`, `IMapRenderer`).
- **Imagery / Vips** = the raster engine (`Spatial.Imagery.Vips`,
  `IRasterOperations`); `Spatial:Imagery` names flat render sources while
  `Spatial:Raster` names catalogued ImageServer datasets.
- **Scheme vs serving**: the tiling scheme lives in `Spatial.Tiling.*`
  (projection + LOD math); the host's cache/render orchestration lives in
  `Spatial.Host.TileServing` (`TileService`, `ITileCache` implementations).

## Implementations and composition

| Project | Owns | Package |
| --- | --- | --- |
| `Spatial.Rendering.Skia` | `IMapRenderer` | SkiaSharp 4.152.0 (+ Linux native assets), SkiaSharp.HarfBuzz 4.152.0 (HarfBuzzSharp 14.2.1.200), Svg.Skia 5.2.3 |
| `Spatial.Imagery.Vips` | `IRasterOperations`, `IRasterCatalogue` | NetVips 3.2.0 + NetVips.Native 8.18.6 |
| `Spatial.Tiling.WebMercator` | `ITileScheme` (EPSG:3857 XYZ) | none |
| `Spatial.Tiling.Mvt` | `IVectorTileService` (MVT 2.1 feature/attribute encoding) | none |

Neither references the other (ADR-0033); `Spatial.Host` wires them. Skia plus
native types are contained in their owning assembly.

Pipeline stages (all in `Spatial.Rendering.Skia` unless noted):

1. **Compile** — `StyleCompiler` → `CompiledStyle` (cached by style hash).
2. **Read** — `FeaturePipeline` describes the dataset and pushes the viewport
   bbox into `IFeatureStore.QueryAsync`, once per dataset.
3. **Place** — `GeometryPipeline.Place` clips source geometry to the
   viewport's source-CRS image (so a dataset reaching the poles stays inside
   Web Mercator's domain) and transforms source SRID → viewport CRS.
4. **Shape** — `GeometryPipeline.SimplifyAndCull` simplifies in screen units
   (`unitsPerPixel / 2`) and drops geometry outside the viewport.
5. **Rasterize** — `SkiaVectorRasterizer` clears the background and draws the
   scene to premultiplied RGBA (`SkiaPathBuilder` / `SkiaPaintFactory`).
   Symbol layers are shaped and placed by `SkiaSymbolRasterizer` over the
   embedded `BundledFont` and `SpriteRegistry` (ADR-0049).
6. **Compose + encode** — `RasterComposer` calls `IRasterOperations`; without
   an imagery pipeline the vector-only path encodes PNG/JPEG with Skia.
7. `MapRenderer` is a thin facade; `SceneBuilder` builds the scene;
   `ZoomEstimator` maps viewport resolution to a zoom for style windows.

Tiles (ADR-0046) sit on top: `TileService` (host) resolves an `ITileScheme`
by id, checks `ITileCache`, derives the tile viewport from `ITileScheme.Bounds`
and calls `IMapRenderer` per tile; a batch runs an ordered tile list through
bounded parallelism. The memory cache is the host's `InMemoryTileCache`
(LRU, byte + entry bounds); `FileTileCache` (T-001, same bounds, LRU by
file time) persists tiles under a shared directory so they survive a
restart and are shared between hosts. MVT bytes use the same `ITileCache`
key and cache implementations. The version in `TileCacheKey` is a SHA-256 of
the service/layer/encoding request.

## Style document (documented MapLibre subset)

Canonical document is the MapLibre style spec JSON the workbench writes.
A map may also persist a per-layer style fragment in the same dialect
(ADR-0047): each `MapLayer.Style` is a JSON array of style-layer
objects without `id`/`source-layer`, and the host injects those (plus the
layer's dataset) when it assembles the render document.
Supported layers: `background`, `fill`, `line`, `circle`, `symbol`. Per-layer keys:
`minzoom`, `maxzoom`, `layout.visibility`, `filter`, `paint`.

- Paint: `background-color/-opacity`; `fill-color/-opacity`,
  `fill-outline-color`, `fill-outline-width`; `line-color/-opacity/-width/`
  `-dasharray/-cap/-join`; `circle-color/-radius/-opacity/-stroke-color/`
  `-stroke-width`; `text-color/-opacity`, `text-halo-color/-width`.
- Every documented colour, opacity and size property above also accepts a
  **MapLibre expression** instead of a constant (T-u2x.19); the constant
  remains the value an expression falls back to when it has none for a
  feature. Symbol *layout* keys stay constant — expressions there are a
  separate bead.
- Symbol layout (ADR-0049, ADR-0080): `text-field` (a `{attribute}` template,
  newlines split a multi-line label), `text-font` (a fallback-ordered list of
  face names), `text-size`, `text-anchor`, `text-offset` (ems), `text-padding`,
  `text-allow-overlap`, `icon-image` (a bundled sprite name), `icon-size`,
  `icon-allow-overlap`, `symbol-placement` (`point` | `line`), `symbol-spacing`,
  `symbol-sort-key`, `symbol-allow-overlap`, `symbol-ignore-placement`,
  `text-transform` (`none` | `uppercase` | `lowercase`), `text-letter-spacing`
  (ems), `text-line-height` (ems) and `text-rotate` (degrees). Unknown keys
  and unknown values are rejected.
- Fonts come only from the embedded Noto Sans 2.003 family (OFL-1.1) — Regular,
  Bold, Italic and Bold Italic, each digest-pinned; sprites only from the
  embedded `Resources/Sprites/*.svg` (the `default-marker` and the
  `marker-circle`/`-square`/`-diamond`/`-triangle`/`-ring` set), never a URL.
  `icon-image` resolves at draw time; an unknown name is a typed error.
- `text-font` is never validated as a style key. Each name walks a documented
  chain — bundled family at the requested weight/style, else the nearest
  bundled weight (heavier first, then lighter) in the requested or upright
  style, else the next name — and a family the bundle does not carry falls to
  the default face (Noto Sans Regular). A missing face substitutes; it does not
  fail the render.
- Label placement is a deterministic greedy first-fit over **ordered
  candidates** (ADR-0080): a point offers its position and then the four anchor
  offsets; `symbol-placement: line` offers a candidate every `symbol-spacing`
  pixels along the line, aligned to the local direction, then the same
  candidates in reverse. The first candidate that places anything wins; a
  feature with no free candidate is dropped.
- Competition is decided in a **placement pass of its own**, in the order
  `symbol-sort-key`, then the style's document order, then the within-layer
  identity/envelope-centre order — a total order, so repeated runs and store
  page order cannot change it. The pixels still composite in document order.
  `symbol-allow-overlap` (inheriting `text-`/`icon-allow-overlap` when unset)
  and `symbol-ignore-placement` bypass the collision test; an ignored label
  neither blocks nor is blocked.
- A candidate is a `(x, y, degrees)` frame: the label box, the icon box and
  both draws happen in it, so a line label runs along its line and a
  `text-offset` on it reads perpendicular. The text transforms are applied to
  the shaped string: `text-transform` before shaping, `text-letter-spacing` as
  extra advance between the runs the HarfBuzz cluster map produced,
  `text-line-height` as the baseline advance between the lines of a multi-line
  field, and `text-rotate` as a fixed rotation about the anchor.
- Filter operators: `==`, `!=`, `has`, `!has`, `in`, `all`, `any`, `none`,
  `!` over `IFeature` attributes (never SQL). A filter that nests an
  expression anywhere is read as an **expression filter** instead, and must
  yield a boolean; the two dialects never mix halfway.
- An unknown layer type, paint property, filter expression or visibility is a
  typed `invalid.arguments` naming it — unsupported input is rejected, never
  flattened.

### Expressions

A partial MapLibre expression dialect, compiled once per style document into an
immutable node tree and evaluated per feature:

- **Terms**: literals and `["literal", …]`, `["get", name]`,
  `["get", name, fallback]`, `["has", name]`, `["zoom"]`, `["id"]`,
  `["geometry-type"]`, `["var", name]`, `["let", …]`.
- **Operators**: `==` `!=` `<` `<=` `>` `>=`; `all` `any` `!`; `in`; `+` `-`
  `*` `/` `%` and unary `-`; `concat`; `case`; `match` (with a label list);
  `coalesce`; `step`; `interpolate` over `["linear"]` or
  `["exponential", base]`, interpolating numbers and colours per channel.
- **Typed, not coerced**: the compiler rejects an expression whose static type
  does not fit the property (`'circle-color' expects a colour, but … yields a
  number`) and a value discovered per feature is rejected the same way at
  render time. A number is *not* a colour, so the MapLibre idiom of ramping a
  number into a colour needs colour stops; `to-color` and `at-interpolate`
  are not served.
- **An expression with no value for a feature** (a missing attribute) falls
  back to the property's documented constant rather than failing the render.
- **Once per feature, not per property**: the scene builder keeps one
  `ExpressionScope` per feature for the whole style, and identical expression
  text compiles to one shared node, so an expression read by several
  properties across several layers is evaluated once per feature. The counters
  are measured — `MapRenderer.LastStatistics` (internal) and the
  `Render_ExpressionPointsTile` benchmark — not asserted in a comment.

## Host routes and configuration

| Route | Result |
| --- | --- |
| `POST /api/render` | image bytes (`Content-Type` by format) + `X-Raster-Width/Height/Format` |
| `GET /api/render/capabilities` | advertised formats, pixel cap, imagery source names |
| `POST /api/maps/{name}/render` | render a map's datasets with its persisted per-layer style (ADR-0047); same encoding options, no inline style/layers |
| `POST /api/render/tiles/{z}/{x}/{y}.{format}` | one cache-aware tile (binary) + `X-Tile-Cached` |
| `POST /api/render/tiles/batch` | ordered raster tile list (Base64) with cache dispositions |
| `GET /api/maps/{name}/tiles/mvt/{z}/{x}/{y}.pbf` | live MVT bytes for a map's feature layers (`Tiles` service) |
| `GET /ogc/{name}/tiles` | OGC API Tiles landing page for the map collection |
| `GET /ogc/{name}/tiles/collections/{id}/tiles/{matrixSet}/{z}/{x}/{y}.pbf` | OGC-negotiated live MVT tile over the shared scheme/cache |
| `GET /ogc/{name}/tiles/collections/{id}/tiles/{matrixSet}/TileJSON` | OGC TileJSON for the map's vector layers and matrix set |
| `GET /api/render/tiles/capabilities` | schemes, LODs, default scheme, batch cap |
| `DELETE /api/render/cache` | explicit tile-cache invalidation |

```jsonc
"Spatial": {
  "Rendering": { "Formats": ["png","jpeg","webp"], "MaxPixels": 16777216, "MaxLayers": 32 },
  "Imagery":   { "Sources": [ { "name": "basemap", "path": "/data/basemap.tif" } ] },
  "Tiles":     { "DefaultScheme": "webmercator", "MaxTilesPerBatch": 64, "Concurrency": 0,
                 "Cache": { "Provider": "memory", "Root": "", "MaxBytes": 67108864, "MaxEntries": 4096 } }
}
```

`Cache.Provider: file` with a shared `Cache.Root` selects the persistent
tile cache (T-001, ADR-0046): tiles survive a host restart and are shared
between hosts on the same root; storage failures are `store.unavailable`.

Imagery `Source` is a configured name/path, never a caller-supplied URL
(SSRF). Failures map through `ErrorMapper`: `invalid.arguments` → 400,
`not.found` → 404, `store.unavailable` → 503, cancellation → 499.

## Testing and traps

- Unit: style resolution tables, filter operators, geometry place/shape,
  rasterized pixel assertions, the render facade with fakes, and the libvips
  traps (sRGB interpretation before `composite2`, equal band counts,
  premultiplied input, stride padding).
- Expressions: a MapLibre conformance table (every served operator, with and
  without an interpolation exponent, plus zoom/geometry-type/id/let-var),
  the typed rejections (a number for a colour property, an attribute of the
  wrong type, a negative size, a filter that is not boolean, an unbound `var`,
  a non-ascending stop), a per-feature render proving data-driven paint and
  expression filters reach the raster, and a measurement that the second
  layer's read of an expression is a memo hit rather than a second
  evaluation. The committed golden render is unchanged: a style that uses no
  expression compiles to the same constant recipe it always did.
- Host: content type/format, error mapping, pixel cap.
- Clients: `.NET` `SpatialClient.RenderAsync` / `RenderCapabilitiesAsync` /
  `Tiles.VectorTileAsync`; TypeScript `render` / `renderCapabilities` /
  `vectorTile` (binary via `arrayBuffer`).
- `Spatial.Imagery.Vips` un-premultiplies the vector buffer so every layer in
  the libvips chain is straight (non-premultiplied) before compositing.
- Tiles: Web-Mercator LOD math, the G1 LOD replay (resolution/scale) and
  envelope-equality proof, cache get/set/eviction/invalidation, the tile
  version fingerprint, `TileService` order/batch-cap/cancellation, the HTTP
  tile routes (hit/miss, formats, schemes, batch), and MVT success/failure/
  cancellation. The neutral route is `GET /api/maps/{name}/tiles/mvt/...`;
  the Esri projection is `.../MapServer/vectorTile/...` (and the
  `VectorTileServer/tile/...` spelling); OGC API Tiles landing, collection,
  TileJSON and negotiated MVT routes are covered by `OgcApiTilesTests` and
  `OgcVectorTileServiceTests`.
- Global data: a dataset whose extent reaches a pole is clipped to the
  viewport before reprojection, so Web-Mercator tiles and WMS capabilities
  never ask the transform service to project ±90° (`PolarRenderTests`).
- Labels/symbols: property resolution (including typed rejection of
  unsupported keys and unknown *values*), text-anchor/offset placement, halo,
  collision (a taken point falls back to the next candidate, a feature with no
  free candidate is dropped, `*-allow-overlap` places all), byte-identical
  repeated runs, feature-order independence, the embedded-font digests, the
  sprite set, and committed golden renders under
  `tests/fixtures/rendering/golden/` compared exactly on CI and with a
  bounded tolerance elsewhere.
- Label placement depth (ADR-0080): the candidate generators (point offsets,
  per-spacing line positions, their angles, multi-line parts, degenerate
  geometry), the priority order (`symbol-sort-key` beating document order
  across layers, the identity/envelope-centre tie-break, the overlap and
  ignore-placement bypasses), line placement with a perpendicular
  `text-offset`, the four text transforms, the font face registry (every
  bundled digest, the family/weight/style parse, nearest-weight matching, the
  fallback to the default face and the per-render shaper session), and a second
  golden render covering line placement, a bold face, a transform and a sprite.

## Not implemented

Offline `.vtpk` packaging and `exportTiles` are not part of this phase;
live MVT, OGC API Tiles and the existing raster tile surface are supported.
A GPU backend is not planned. Within the symbol subset, curved or
variable-along-a-line placement, sprite sheets, `text-rotation-alignment`,
`symbol-z-order` and expressions in symbol *layout* are not claimed, and the
expression dialect serves neither `to-color` nor `at-interpolate`.
