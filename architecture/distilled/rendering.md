# Raster Rendering

The condensed form of ADR-0044 (and ADR-0049 for labels/symbols): turn the
engine's vector outputs into styled raster output over a NetVips imagery
pipeline. Read the
ADR for the decision and the research at
`../../research/rendering/README.md` for the measured baseline and the
libvips traps; this is the as-built shape.

## Contract surface (`Spatial.PluginSdk`)

Core/framework types only — no Skia, no NetVips (ADR-0005). The value types
sit in the root `Spatial.PluginSdk` namespace; the wire DTOs in
`Spatial.PluginSdk.Http`.

| Type | Role |
| --- | --- |
| `IMapRenderer.RenderAsync(MapRenderRequest)` | the whole pipeline → `RasterImage` |
| `IRasterOperations.ReadAsync(RasterReadRequest)` | load/normalise one configured imagery source |
| `IRasterOperations.CompositeAsync(RasterCompositeRequest)` | blend a bottom-to-top layer stack, encode |
| `IRasterCatalogue` (ADR-0051) | raster dataset metadata (block/pyramid structure included), catalog items/query/identify, warped/encoded export (overview-aware) and raw file listing/reading for the ImageServer |
| `RasterViewport(Envelope Bounds, int Width, int Height, string Crs)` | the viewport, x-first |
| `RasterBuffer` / `RasterImage` | raw (premultiplied RGBA by default) pixels / encoded bytes |
| `RasterLayer`, `RasterBufferLayer`, `RasterSourceLayer` | the composite stack entries |
| `MapLayerSource(Dataset, Features, Catalogue, Filter, Time)` | a resolved keyed store + catalogue, with an optional `MapTimeExtent` render filter (ADR-0056) |
| `RasterFormat`, `RasterPixelFormat`, `RasterBlend` | png/jpeg/webp/tiff, rgba8888/rgb888, blend modes |
| `ITileScheme` + `TileCoordinate`/`TileLevel` | pluggable tiling: address → projected extent + LODs (ADR-0046) |
| `ITileCache` + `TileCacheKey` | content-addressed tile cache; ownership is the implementation's (ADR-0046) |

The renderer receives **resolved** services in the request (no DI/service
location), so it is unit-testable with fakes; `Spatial.Host` resolves each
layer's keyed store and catalogue at the edge.

## Implementations and composition

| Project | Owns | Package |
| --- | --- | --- |
| `Spatial.Rendering.Skia` | `IMapRenderer` | SkiaSharp 4.152.0 (+ Linux native assets), SkiaSharp.HarfBuzz 4.152.0 (HarfBuzzSharp 14.2.1.200), Svg.Skia 5.2.3 |
| `Spatial.Imagery.Vips` | `IRasterOperations`, `IRasterCatalogue` | NetVips 3.2.0 + NetVips.Native 8.18.6 |
| `Spatial.Tiling.WebMercator` | `ITileScheme` (EPSG:3857 XYZ) | none |

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
restart and are shared between hosts. The version in `TileCacheKey` is a
SHA-256 of the style/layer/imagery/encoding request.

## Style document (documented MapLibre subset)

Canonical document is the MapLibre style spec JSON the workbench writes.
A publication may also persist a per-layer style fragment in the same dialect
(ADR-0047): each `PublicationLayer.Style` is a JSON array of style-layer
objects without `id`/`source-layer`, and the host injects those (plus the
layer's dataset) when it assembles the render document.
Supported layers: `background`, `fill`, `line`, `circle`, `symbol`. Per-layer keys:
`minzoom`, `maxzoom`, `layout.visibility`, `filter`, `paint`.

- Paint: `background-color/-opacity`; `fill-color/-opacity`,
  `fill-outline-color`, `fill-outline-width`; `line-color/-opacity/-width/`
  `-dasharray/-cap/-join`; `circle-color/-radius/-opacity/-stroke-color/`
  `-stroke-width`; `text-color/-opacity`, `text-halo-color/-width`.
- Symbol layout (ADR-0049): `text-field` (a `{attribute}` template),
  `text-font` (the bundled Noto Sans aliases only), `text-size`,
  `text-anchor`, `text-offset` (ems), `text-padding`, `text-allow-overlap`,
  `icon-image` (a bundled sprite name), `icon-size`, `icon-allow-overlap`.
  `symbol-placement: line`, expressions and unknown keys are rejected.
- Fonts come only from the embedded Noto Sans Regular 2.003 (OFL-1.1);
  sprites only from the embedded `Resources/Sprites/*.svg` (the bundled
  `default-marker`), never a URL. `icon-image` resolves at draw time; an
  unknown name is a typed error.
- Label placement is a deterministic greedy first-fit: layers in document
  order, features sorted by identity then source envelope centre, with
  `*-allow-overlap` bypassing the collision test.
- Filter operators: `==`, `!=`, `has`, `!has`, `in`, `all`, `any`, `none`,
  `!` over `IFeature` attributes (never SQL).
- An unknown layer type, paint property, filter expression or visibility is a
  typed `invalid.arguments` naming it — unsupported input is rejected, never
  flattened.

## Host routes and configuration

| Route | Result |
| --- | --- |
| `POST /api/render` | image bytes (`Content-Type` by format) + `X-Raster-Width/Height/Format` |
| `GET /api/render/capabilities` | advertised formats, pixel cap, imagery source names |
| `POST /api/publications/{name}/render` | render a publication's datasets with its persisted per-layer style (ADR-0047); same encoding options, no inline style/layers |
| `POST /api/render/tiles/{z}/{x}/{y}.{format}` | one cache-aware tile (binary) + `X-Tile-Cached` |
| `POST /api/render/tiles/batch` | ordered tile list (Base64) with cache dispositions |
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
- Host: content type/format, error mapping, pixel cap.
- Clients: `.NET` `SpatialClient.RenderAsync` / `RenderCapabilitiesAsync`;
  TypeScript `render` / `renderCapabilities` (binary via `arrayBuffer`).
- `Spatial.Imagery.Vips` un-premultiplies the vector buffer so every layer in
  the libvips chain is straight (non-premultiplied) before compositing.
- Tiles: Web-Mercator LOD math, cache get/set/eviction/invalidation, the tile
  version fingerprint, `TileService` order/batch-cap/cancellation, and the
  HTTP tile routes (hit/miss, formats, schemes, batch).
- Global data: a dataset whose extent reaches a pole is clipped to the
  viewport before reprojection, so Web-Mercator tiles and WMS capabilities
  never ask the transform service to project ±90° (`PolarRenderTests`).
- Labels/symbols: property resolution (including typed rejection of
  unsupported keys and unknown fonts), text-anchor/offset placement, halo,
  collision (first wins, `*-allow-overlap` places all), byte-identical
  repeated runs, feature-order independence, the embedded-font hash, the
  embedded sprite registry, and a committed golden render under
  `tests/fixtures/rendering/golden/` compared exactly on CI and with a
  bounded tolerance elsewhere.

## Not implemented

A persistent/shared tile cache (the `ITileCache` contract is ready for it);
a GPU backend is not planned. The GeoServices MapServer (`export`/`tile`,
ADR-0048) is implemented; a neutral `/api/publications/{name}/tiles` route is
not. Within the symbol subset, line placement, expressions, sprite sheets and
text transforms are not claimed.
