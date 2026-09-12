# 2D rendering + imagery autoresearch

**Question.** What modern 2D rendering library should turn the spatial
engine's vector outputs, styled with CSS-like rules, into raster output — and
how should NetVips imagery compose into one high-performance pipeline built
from the engine's existing services?

**Answer (short).** **SkiaSharp for vector rasterization, NetVips for imagery
and composition**, both hidden behind `Spatial.PluginSdk` interfaces and
wired by DI in `Spatial.Host`. The style document is the MapLibre style spec
(so the browser workbench and the server agree); a CSS-ish authoring layer
lowers to the same compiled style model. A runnable spike proves the
pipeline end to end and measures it.

This directory is the research half of the change. The decision record is
`architecture/decisions/ADR-0044-raster-rendering-pipeline.md` (proposed);
the production shape is a later, separately-verified change.

## Method

1. **Probe the field** with `python3 research/rendering/probe.py` (NuGet +
   GitHub facts: maintenance, reach, licence, archived).
2. **Filter on the repo's hard walls** (ADR-0005/0033): .NET 10, permissive
   licence, third-party types can stay inside one implementation project,
   no cross-implementation references.
3. **Spike the survivor** in `spike/RenderSpike`: real `Spatial.Core`
   geometry → `ICoordinateTransforms` → `IGeometryOperations.Simplify` →
   Skia raster → NetVips composite/encode, then measure it.

Environment for the numbers below: .NET 10.0.401, 24 cores, SkiaSharp
4.152.0, NetVips 3.2.0 / libvips 8.18.6, Release build.

## Candidate field

| Candidate | Kind | Licence | .NET binding | Verdict |
| --- | --- | --- | --- | --- |
| **SkiaSharp** | vector rasterizer | MIT | first-party, `4.152.0`, active | **chosen** |
| **NetVips** | imagery / compositing | MIT binding over libvips LGPL-2.1 | first-party, `3.2.0`, active | **chosen** |
| Svg.Skia | SVG (icons/symbols) | MIT | first-party, `5.2.3`, active | complementary |
| ExCSS | CSS parser (authoring) | MIT | first-party, `4.3.2` | optional front-end |
| MapLibre Native | full map renderer | BSD-2 | **none maintained** | rejected |
| Mapnik | full map renderer | LGPL-2.1 | NuGet binding `2.1.1`, stale | rejected |
| Vello | GPU vector renderer | Apache-2.0 | **none (.NET)** | rejected (watch) |
| SixLabors.ImageSharp.Drawing | managed raster/vector | Split Licence (source-available) | first-party | rejected (licence, perf) |
| Magick.NET | raster + MVG draw | Apache-2.0 | first-party | rejected (not a path rasterizer) |
| Mapsui / SharpMap | map components | MIT / LGPL | first-party / stale | rejected (component, not kernel) |
| System.Drawing.Common | vector on GDI+ | MIT | Windows-only | rejected |

### Why SkiaSharp

- The Skia engine behind Chrome/Android/Flutter; mature, fast, AA-correct
  path fills/strokes, dashes, gradients, clipping, blend modes, colour
  management. GPU back ends (Ganesh today, Graphite emerging) exist when
  CPU stops being enough.
- **Actively modern**: v4 obsoletes the mutable `SKPath` drawing calls in
  favour of `SKPathBuilder` — the spike uses it; that churn is a health
  signal, not a warning sign.
- 330M+ NuGet downloads; MIT; native assets per-RID including
  `Linux.NoDependencies`, so containers do not need a system Skia.
- Text shaping is a separate, similarly maintained `SkiaSharp.HarfBuzz`.
- Crucially, Skia types are contained: one implementation project draws and
  returns a raw RGBA buffer; nothing Skia-shaped crosses a contract
  (ADR-0005 applied to rendering).

### Why NetVips

- libvips is the reference large-imagery pipeline: demand-driven, threaded,
  low-memory streaming, colour management, mosaicking, blending, many
  encoders. Exactly the "imagery" verb set the question asks for.
- NetVips 3.2.0 is MIT and tracks libvips 8.18.x (the installed 8.18.6).
- It takes the vector rasterizer's RGBA buffer directly
  (`Image.NewFromMemory`) and composes/encodes in one lazy chain, so the
  vector result is never written to disk before final output.
- libvips itself is LGPL-2.1 and is shipped as a shared library by
  `NetVips.Native`, which satisfies LGPL dynamic-linking obligations.

### Why not MapLibre Native (the parity stronghold)

It is the honest answer to "render exactly what the browser workbench
renders", is BSD-2 and active — but it has **no maintained .NET binding**.
Using it means a C++/FFI build, a Node sidecar, or a separate service, which
fights the in-process DI model (ADR-0033) and the "host owns no spatial
logic / no second process" shape. Keep it as the fallback if style parity
ever outranks everything else; the recommended path instead makes the
MapLibre style spec the *shared style document* while Skia draws it.

## Recommended architecture

Two new SDK contracts over core types, two implementation projects, Host
wiring. The renderer depends on the imagery *interface*, never on NetVips
directly, so the engine keeps "implementations never depend on each other".

```
Spatial.PluginSdk
  IMapRenderer          RenderAsync(MapRenderRequest) -> RasterImage
  IRasterOperations     CompositeAsync(RasterComposite) -> RasterImage   // imagery verbs
  DTOs: MapRenderRequest, RasterViewport, RasterComposite, RasterLayer,
        RasterImage, RasterFormat, RasterBlend, RasterPixelFormat, TileCoordinate

Spatial.Rendering.Skia    implements IMapRenderer      (SkiaSharp inside)
  ├─ FeaturePipeline      IDataCatalogue + IFeatureStore read, bbox pushdown
  ├─ GeometryPipeline     ICoordinateTransforms + IGeometryOperations
  ├─ StyleCompiler        style document -> per-layer DrawPlan (cached)
  ├─ SkiaVectorRasterizer geometry -> SKPath -> pixels (RGBA)
  └─ calls IRasterOperations for imagery + encode

Spatial.Imagery.Vips      implements IRasterOperations (NetVips inside)

Spatial.Host              registers both; keyed DI selects the renderer
```

### The pipeline

```
MapRenderRequest
 1 Resolve     viewport CRS/scale; datasets via IDataCatalogue.DescribeAsync
 2 Plan        StyleCompiler: style doc + schema -> per-layer DrawPlan
               (geometry kind, zoom window, paint recipe, attribute filter)
 3 Read        IFeatureStore.QueryAsync(dataset, bbox, filter) per layer,
               one page set per zoom batch (not per tile)
 4 Shape       IGeometryOperations.Simplify(tolerance ~= unitsPerPixel/2),
               clipped to the viewport
 5 Place       ICoordinateTransforms.Transform(source CRS -> viewport CRS)
 6 Rasterize   Skia: SKPathBuilder + paints; labels via HarfBuzz + placement
 7 Compose     IRasterOperations: imagery floor (and hillshade) Over vector RGBA
 8 Cache       content-addressed by (style hash, dataset version, z/x/y)
```

Each stage is the engine's existing service; the renderer adds only the
drawing and the cache. Tiles are independent and stateless, so the tile
batch parallelises trivially, and the same pipeline answers a one-off
`bbox + width + height` export request.

### Styling: MapLibre style spec, CSS as authoring sugar

- **Serialized style = MapLibre style spec JSON**, the dialect the workbench
  already writes (`MapScreen.tsx`). Sharing the document is the cheapest
  path to visual agreement between client and server, and it covers
  `background` / `fill` / `line` / `circle` / `symbol` with zoom- and
  data-driven expressions.
- **Authoring convenience = a CSS-ish layer**: selectors on layer id/class
  and declarations (`fill`, `stroke`, `stroke-width`, `stroke-dash`,
  `circle-radius`, …) compiled by ExCSS onto the same compiled style model.
  The spike implements a deliberately tiny subset to prove the lowering;
  production should use ExCSS, not that toy parser.
- Do **not** invent a third dialect. The compiled style model is internal;
  both front-ends lower to it.

### Performance levers (measured or structural)

- **Push bbox into the store.** `IFeatureStore.QueryAsync(bbox)` already
  exists; read once per zoom batch and reuse across neighbouring tiles.
- **Simplify per zoom, cache it.** `IGeometryOperations.Simplify` dominates
  the non-raster cost at dense zooms (27–49 ms for 32k–128k vertices in the
  spike); it must not run per tile.
- **Parallelise tiles**, bounded by cores; expect near-linear scaling
  (spike: ~13× on 24 threads).
- **Trim paint cost**: dashed strokes measured **1.49×** a solid stroke for
  32k vertices; drop dashes on dense layers, and consider disabling AA for
  hairlines.
- **NetVips is lazy**: load → resize → composite → encode is one pipelined
  request; keep it so. `ThumbnailImage` for imagery, sequential access.
- **GPU later, with an ADR**: Skia Ganesh/Graphite is the escape hatch; it
  needs headless EGL/Vulkan on the server and measured benefit (ADR-0012/0021
  keep the host JIT).

## Spike results

`spike/RenderSpike` renders core geometry to a styled 512×512 tile and
composites it over NetVips imagery. Per-stage mean over 20 iterations,
1.79–48.9 ms simplify / 9.06–651.41 ms raster / 9.68–40.19 ms compose.
These are indicative single-machine numbers, not a controlled benchmark
suite; the durable results are the ratios (parallel scaling, dash cost):

| case (512²) | vertices | simplify | raster | compose | pipe ms | pipe/s |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| lines=50 verts=32 | 1,600 | 1.79 ms | 9.06 ms | 9.68 ms | 20.5 | 48.7 |
| lines=500 verts=64 | 32,000 | 41.4 ms | 145.3 ms | 40.2 ms | 226.9 | 4.4 |
| lines=2000 verts=64 | 128,000 | 48.9 ms | 651.4 ms | 21.1 ms | 721.4 | 1.4 |

- **Parallel tiles:** 96 tiles of the 32k-vertex case completed end-to-end in
  1.47 s across 24 threads → **65.4 tiles/s**, ~13× the single-thread rate.
- **Tuning lever:** dashed 139.6 ms vs solid 94.0 ms for 32k vertices (1.49×).
- Compose time is roughly feature-count-independent (10–40 ms): it is
  pixel/encode-bound, which is the shape you want — vector cost scales with
  the data, imagery cost with the frame.
- These random long-line networks are a deliberately harsh case; real
  per-zoom data arrives pre-simplified and much lighter.

The rendered sample (`/tmp/render-spike/sample.png`) shows a park polygon,
two dashed roads and four city points composed over the imagery floor.

## Integration gotchas found by the spike

- **libvips needs an interpretation before `composite2`.** A buffer from
  `NewFromMemory` is `multiband`; compositing fails with
  *"no known route from 'multiband' to 'srgb'"*. Tag inputs
  `Copy(interpretation: Srgb)` first.
- **Both composite inputs must have the same band count.** `Image.Xyz` is
  2-band `uint`, not RGB; build the imagery floor from a single-band field
  (`Sines` + `Linear` + `Bandjoin`) or a real file, then `AddAlpha`.
- **Skia RGBA is premultiplied.** Pass `premultiplied: true` to
  `Composite`; the ordinary (`false`) path would double-premultiply.
- **Skia v4 obsoletes `SKPath` mutators** (`MoveTo`/`LineTo`/`Close`) in
  favour of `SKPathBuilder` + `Detach()`.
- **Row stride:** `SKBitmap` rows are padded; widths that are not multiples
  of four need stride-aware copying into the libvips buffer.

## Phased plan

1. **ADR + contracts** (small): `IMapRenderer` + `IRasterOperations` + DTOs
   in `Spatial.PluginSdk`; architecture-test allowlist entries for the two
   new projects and their packages; distilled-doc updates.
2. **Vector first**: `Spatial.Rendering.Skia` renders `fill`/`line`/`circle`
   from a MapLibre-subset style, no imagery, output PNG. Host route +
   workbench "export PNG" action. This is already useful.
3. **Imagery** `Spatial.Imagery.Vips`: composite over a raster basemap and
   encode; the renderer consumes `IRasterOperations`.
4. **Tiles + cache**: tile addressing, parallel batch, content-addressed
   cache keyed by style/dataset hashes.
5. **Labels/symbols**: `SkiaSharp.HarfBuzz` shaping + a placement/collision
   pass + `Svg.Skia` for sprite symbols (the largest remaining chunk).
6. **Only then** consider a GPU backend or MapLibre Native parity, each with
   its own measured ADR.

## Open questions

- **Label collision** is the one place a naive Skia renderer diverges
  visibly from MapLibre; its placement algorithm is non-trivial.
- **Style-spec coverage**: expressions, `*-pattern`, `symbol-placement`,
  raster sources. Fixed subset first, documented compatibility.
- **Cache invalidation** across dataset versions and style changes.
- **CRS**: Web Mercator + plate carrée first; other viewports need
  reprojection of everything and are their own decision.
- **Licence hygiene**: libvips LGPL (dynamic link), ImageSharp avoided.

## Reproduce

```bash
python3 research/rendering/probe.py                 # candidate facts
cd research/rendering/spike/RenderSpike
dotnet run -c Release                               # sample -> /tmp/render-spike/sample.png
dotnet run -c Release -- --bench                    # stage + parallel numbers
dotnet run -c Release -- --imagery /path/base.tif   # composite over real imagery
```
