# Raster Rendering

The condensed form of ADR-0044 and `../rendering-implementation-plan.md`
(R0–R3): turn the engine's vector outputs into styled raster output over a
NetVips imagery pipeline. Read the ADR for the decision and the research at
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
| `RasterViewport(Envelope Bounds, int Width, int Height, string Crs)` | the viewport, x-first |
| `RasterBuffer` / `RasterImage` | raw (premultiplied RGBA by default) pixels / encoded bytes |
| `RasterLayer`, `RasterBufferLayer`, `RasterSourceLayer` | the composite stack entries |
| `MapLayerSource(Dataset, Features, Catalogue, Filter)` | a resolved keyed store + catalogue |
| `RasterFormat`, `RasterPixelFormat`, `RasterBlend` | png/jpeg/webp/tiff, rgba8888/rgb888, blend modes |
| `ITileScheme` + `TileCoordinate`/`TileLevel` | pluggable tiling: address → projected extent + LODs (ADR-0046) |
| `ITileCache` + `TileCacheKey` | content-addressed tile cache; ownership is the implementation's (ADR-0046) |

The renderer receives **resolved** services in the request (no DI/service
location), so it is unit-testable with fakes; `Spatial.Host` resolves each
layer's keyed store and catalogue at the edge.

## Implementations and composition

| Project | Owns | Package |
| --- | --- | --- |
| `Spatial.Rendering.Skia` | `IMapRenderer` | SkiaSharp 4.152.0 (+ Linux native assets) |
| `Spatial.Imagery.Vips` | `IRasterOperations` | NetVips 3.2.0 + NetVips.Native 8.18.6 |
| `Spatial.Tiling.WebMercator` | `ITileScheme` (EPSG:3857 XYZ) | none |

Neither references the other (ADR-0033); `Spatial.Host` wires them. Skia plus
native types are contained in their owning assembly.

Pipeline stages (all in `Spatial.Rendering.Skia` unless noted):

1. **Compile** — `StyleCompiler` → `CompiledStyle` (cached by style hash).
2. **Read** — `FeaturePipeline` describes the dataset and pushes the viewport
   bbox into `IFeatureStore.QueryAsync`, once per dataset.
3. **Place** — `GeometryPipeline.Project` transforms source SRID → viewport CRS.
4. **Shape** — `GeometryPipeline.SimplifyAndCull` simplifies in screen units
   (`unitsPerPixel / 2`) and drops geometry outside the viewport.
5. **Rasterize** — `SkiaVectorRasterizer` clears the background and draws the
   scene to premultiplied RGBA (`SkiaPathBuilder` / `SkiaPaintFactory`).
6. **Compose + encode** — `RasterComposer` calls `IRasterOperations`; without
   an imagery pipeline the vector-only path encodes PNG/JPEG with Skia.
7. `MapRenderer` is a thin facade; `SceneBuilder` builds the scene;
   `ZoomEstimator` maps viewport resolution to a zoom for style windows.

Tiles (ADR-0046) sit on top: `TileService` (host) resolves an `ITileScheme`
by id, checks `ITileCache`, derives the tile viewport from `ITileScheme.Bounds`
and calls `IMapRenderer` per tile; a batch runs an ordered tile list through
bounded parallelism. The initial cache is the host's `InMemoryTileCache`
(LRU, byte + entry bounds); the version in `TileCacheKey` is a SHA-256 of the
style/layer/imagery/encoding request.

## Style document (documented MapLibre subset)

Canonical document is the MapLibre style spec JSON the workbench writes.
Supported layers: `background`, `fill`, `line`, `circle`. Per-layer keys:
`minzoom`, `maxzoom`, `layout.visibility`, `filter`, `paint`.

- Paint: `background-color/-opacity`; `fill-color/-opacity`,
  `fill-outline-color`, `fill-outline-width`; `line-color/-opacity/-width/`
  `-dasharray/-cap/-join`; `circle-color/-radius/-opacity/-stroke-color/`
  `-stroke-width`.
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
| `POST /api/render/tiles/{z}/{x}/{y}.{format}` | one cache-aware tile (binary) + `X-Tile-Cached` |
| `POST /api/render/tiles/batch` | ordered tile list (Base64) with cache dispositions |
| `GET /api/render/tiles/capabilities` | schemes, LODs, default scheme, batch cap |
| `DELETE /api/render/cache` | explicit tile-cache invalidation |

```jsonc
"Spatial": {
  "Rendering": { "Formats": ["png","jpeg","webp"], "MaxPixels": 16777216, "MaxLayers": 32 },
  "Imagery":   { "Sources": [ { "name": "basemap", "path": "/data/basemap.tif" } ] },
  "Tiles":     { "DefaultScheme": "webmercator", "MaxTilesPerBatch": 64, "Concurrency": 0,
                 "Cache": { "MaxBytes": 67108864, "MaxEntries": 4096 } }
}
```

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

## Not implemented (per the plan)

R5 publication resolution and the GeoServices `export`/`tile` seam, R6
labels/symbols, R7 GPU backend, and a persistent/shared tile cache (the
`ITileCache` contract is ready for it).
