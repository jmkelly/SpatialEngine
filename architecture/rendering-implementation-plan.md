# Raster Rendering Implementation Plan

> **Status:** R0–R6 implemented (contracts, Skia vector render, NetVips
> imagery, MapLibre-subset style document, host routes and clients, tiles +
> content-addressed cache, publication/GeoServices seam, labels and symbols);
> R7 (GPU) remains and needs a measured bottleneck.
> Companion to
> `architecture/decisions/ADR-0044-raster-rendering-pipeline.md` (the gating
> decision), ADR-0049 (the label/symbol package boundary) and the research at
> `research/rendering/README.md` (candidate survey, spike, measured baselines,
> integration gotchas). Read the research first — it is where the library
> choice and the NetVips traps are proven.
>
> **Numbering note:** ADR-0041 is the ingest/publications decision and
> ADR-0042/0043 are reserved by `publishing-and-ingest-plan.md`, so the
> renderer takes **ADR-0044**.
>
> **Related plans:** `map-service-plan.md` (M2 Export Map consumes this
> plan), `image-service-plan.md` (I0 raster boundary; `IRasterOperations` is
> the first concrete raster contract), `publishing-and-ingest-plan.md`
> (`PublicationKind.Map` is what the renderer resolves in R5).
>
> **Style baseline:** the MapLibre style specification (the dialect
> `apps/workbench-web/src/screens/MapScreen.tsx` already writes). The
> documented subset — not the whole spec — is the correctness target.

## 1. Goal and non-goals

**Goal.** Turn the engine's vector outputs into styled raster output over a
NetVips imagery pipeline, as an in-process service the host composes:

- a **style document** (MapLibre style JSON, plus an optional CSS-ish
  authoring layer) lowers to a compiled per-layer draw plan;
- **core geometry** is read from keyed stores, transformed, simplified and
  clipped by the existing engine services;
- **SkiaSharp** rasterizes the vector layers into a raw RGBA buffer;
- **NetVips/libvips** blends that buffer over configured imagery and encodes
  the result;
- the whole thing is a cancellable `Task` behind a core-typed SDK contract,
  exposed on the typed host API and (later) the GeoServices `export` seam.

**Non-goals (standing).**

- **No core raster values.** Rasters never become `Spatial.Core` types; only
  raw pixel buffers and encoded images cross contracts (mirrors ADR-0005 and
  the image-service plan's option A).
- **No new spatial algorithms.** Read/shape/place reuse `IFeatureStore`,
  `IDataCatalogue`, `ICoordinateTransforms`, `IGeometryOperations`; the
  renderer adds drawing, not geometry.
- **No MapLibre GL parity claim.** The MapLibre *style document* is shared;
  full renderer parity (expressions, `symbol-placement`, raster sources,
  full collision) is explicitly out of scope per phase.
- **No GPU backend, no external renderer, no sidecar** (ADR-0044; revisit
  only with measured demand and a new ADR).
- **No MapServer/ImageServer conformance in this plan** — that is
  `map-service-plan.md` M2+ and `image-service-plan.md` I2+, which consume
  these contracts.
- **No arbitrary URLs.** Imagery references are configured sources, never
  caller-supplied URLs (SSRF).

## 2. Where it sits (boundaries)

```text
Spatial.Core ──values──┐
                       ├── Spatial.PluginSdk.Rendering (IMapRenderer, IRasterOperations, DTOs)
Spatial.PluginSdk ─────┤             ▲                         ▲
  (contracts)          │             │                         │
                       │   Spatial.Rendering.Skia      Spatial.Imagery.Vips
                       │   (SkiaSharp lives here)      (NetVips lives here)
                       │             │                         │
                       └─────────────┴─────────┬───────────────┘
   IFeatureStore / IDataCatalogue ◄───────────┤ (read + describe)
   ICoordinateTransforms / IGeometryOperations ◄┘ (transform + simplify)
                       Spatial.Host (resolves keyed stores, mounts the route)
```

Rules (enforced by `tests/architecture`):

- Both implementation projects reference **Core + SDK only**; neither
  references the other (ADR-0033).
- SkiaSharp types exist only in `Spatial.Rendering.Skia`; NetVips types only
  in `Spatial.Imagery.Vips` (ADR-0005).
- The renderer receives **resolved** services in its request/source records
  (mirroring `FeatureQueryEngine.QueryAsync(dataset, store, …)`), so it
  contains no DI/service-location and is unit-testable with fakes.
- `Spatial.Host` resolves the keyed store per layer at the edge and passes
  it in.

## 3. Contracts (`Spatial.PluginSdk`)

Core/framework types only. `Envelope` is the viewport bounds; pixel buffers
are `ReadOnlyMemory<byte>`; nothing Skia/NetVips-shaped appears. The types
live in the root `Spatial.PluginSdk` namespace alongside the other service
contracts (`IFeatureStore`, `BoundingBox`), per ADR-0044.

```csharp
public enum RasterFormat { Png, Jpeg, WebP, Tiff }
public enum RasterPixelFormat { Rgba8888, Rgb888 }
public enum RasterBlend { Over, Multiply, Screen, Darken, Lighten }

/// <summary>The render viewport: bounds (x-first), pixel size, output CRS.</summary>
public sealed record RasterViewport(Envelope Bounds, int Width, int Height, string Crs);

/// <summary>A raw pixel buffer; premultiplied RGBA by default (as Skia produces).</summary>
public sealed record RasterBuffer(
    ReadOnlyMemory<byte> Pixels, int Width, int Height, int Stride,
    RasterPixelFormat PixelFormat, bool Premultiplied = true);

/// <summary>An encoded image result (bytes only; the media type travels with it).</summary>
public sealed record RasterImage(byte[] Content, string MediaType, int Width, int Height, RasterFormat Format);

/// <summary>Bottom-to-top input stack for <see cref="IRasterOperations.CompositeAsync"/>.</summary>
public abstract record RasterLayer;
public sealed record RasterBufferLayer(RasterBuffer Buffer, RasterBlend Blend = RasterBlend.Over, double Opacity = 1.0) : RasterLayer;
public sealed record RasterSourceLayer(string Source, RasterBlend Blend = RasterBlend.Over, double Opacity = 1.0) : RasterLayer;

public sealed record RasterReadRequest(string Source, RasterViewport Viewport, RasterFormat Format = RasterFormat.Png);
public sealed record RasterCompositeRequest(
    IReadOnlyList<RasterLayer> Layers, RasterFormat Format = RasterFormat.Png,
    int Quality = 90, string? Background = null, bool Transparent = true);

/// <summary>One styled layer: a resolved store + catalogue plus an optional safe filter.</summary>
public sealed record MapLayerSource(string Dataset, IFeatureStore Features, IDataCatalogue Catalogue, string? Filter = null);

public sealed record MapRenderRequest(
    RasterViewport Viewport,
    string Style,
    IReadOnlyList<MapLayerSource> Layers,
    IReadOnlyList<RasterSourceLayer>? Imagery = null,
    RasterFormat Format = RasterFormat.Png,
    int Quality = 90,
    string? Background = null,
    bool Transparent = true,
    double Scale = 1.0);

/// <summary>Renders styled vector layers over imagery to one encoded image.</summary>
public interface IMapRenderer
{
    Task<RasterImage> RenderAsync(MapRenderRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Imagery verbs over raw buffers: read/normalise a source, blend a stack, encode.</summary>
public interface IRasterOperations
{
    Task<RasterImage> ReadAsync(RasterReadRequest request, CancellationToken cancellationToken = default);
    Task<RasterImage> CompositeAsync(RasterCompositeRequest request, CancellationToken cancellationToken = default);
}
```

As-built deltas from the sketch above: the contracts sit in the root
`Spatial.PluginSdk` namespace (the ADR's wording; a `.Rendering` sub-namespace
tripped the metrics zone-of-pain diagnosis), `RasterCompositeRequest` carries
the `RasterViewport` so configured imagery can be resized to the frame, and
the WebP enum member is spelled `Webp` so the camelCase wire value is `webp`.

HTTP DTOs live in `Spatial.PluginSdk.Http` and use **primitives**, not core
structs (the known STJ struct-binding trap): viewport as
`{minX,minY,maxX,maxY,width,height,crs}`, layer as `{dataset,store,filter}`.
`HostApiJson` already carries the converters for the rest. The host maps the
primitive DTO to `MapRenderRequest` and resolves stores.

## 4. The pipeline

```
MapRenderRequest
 1 Resolve   dataset descriptions (IDataCatalogue.DescribeAsync) → source SRID, schema
 2 Plan      StyleCompiler: style document + schema → per-layer DrawPlan
             (layer type, zoom window, paint recipe, attribute predicate)
 3 Read      IFeatureStore.QueryAsync(dataset, bbox, filter) — bbox pushed down,
             once per zoom batch, reused across neighbouring tiles
 4 Shape     IGeometryOperations.Simplify(tolerance ≈ unitsPerPixel/2), clip
 5 Place     ICoordinateTransforms.Transform(source SRID → viewport CRS)
 6 Rasterize SkiaVectorRasterizer: geometry → SKPathBuilder → paints → RasterBuffer
 7 Compose   IRasterOperations.CompositeAsync([imagery…, RasterBufferLayer(vector)])
 8 Cache     content-addressed by (style hash, dataset version, z/x/y)   [R4]
```

Internal classes of `Spatial.Rendering.Skia` (cohesive per ADR-0040, split
before the metrics gate complains):

| Class | Responsibility |
| --- | --- |
| `FeaturePipeline` | resolve descriptions, bbox query, page flattening |
| `GeometryPipeline` | transform, simplify, clip, envelope culling |
| `StyleCompiler` | style document → `DrawPlan`; cached by style hash |
| `SkiaVectorRasterizer` | the only Skia surface: `SKPathBuilder`, paints, text hooks |
| `RasterComposer` | calls `IRasterOperations`, maps failures |

## 5. Styling

- **Canonical document: MapLibre style spec JSON**, subset ordered by phase:
  `background`, `fill`, `line`, `circle` (R3), then `symbol` (R6). Each
  layer's `source-layer` keys onto `MapLayerSource.Dataset`; `filter`
  lowers onto the existing safe attribute-filter grammar — never SQL.
- **CSS-ish authoring (optional)**: ExCSS parses selectors/declarations into
  the same compiled `DrawPlan`; the spike's toy parser is a proof, not the
  production parser. Authoring sugar only — the serialized form stays
  MapLibre JSON so the workbench and server share one document.
- **Unsupported is rejected, not flattened**: an unknown layer type,
  unsupported expression or unsupported renderer is a typed
  `invalid.arguments` naming the property (mirrors `drawingInfo` handling in
  the map-service plan M4).

## 6. Host mounting, configuration, clients

```jsonc
"Spatial": {
  "Rendering": {
    "Formats": ["png", "jpeg", "webp"],     // webp/tiff gated on libvips support
    "MaxPixels": 16777216,                   // width*height cap, per request
    "MaxTilesPerBatch": 64,
    "CacheRoot": "",                         // empty = no cache (R4)
    "StyleRoot": ""                          // optional dir of named style documents
  },
  "Imagery": {
    "Sources": [ { "name": "basemap", "path": "/data/basemap.tif" } ]  // filesystem only
  }
}
```

Routes (typed host API, `Spatial.Host/Api`):

| Route | Phase | Result |
| --- | --- | --- |
| `POST /api/render` | R1 | image bytes (`Content-Type` by format) |
| `GET /api/render/capabilities` | R1 | formats, pixel cap, configured imagery sources |
| `POST /api/render/tiles/{z}/{x}/{y}.{format}` | R4 | one tile, cache-aware (`X-Tile-Cached`) |
| `POST /api/render/tiles/batch` | R4 | ordered tile list, bounded parallelism |
| `GET /api/render/tiles/capabilities` | R4 | registered schemes, LODs, batch cap |
| `DELETE /api/render/cache` | R4 | explicit cache invalidation |

Failures map through `ErrorMapper`: `invalid.arguments` → 400,
`not.found` → 404, `store.unavailable` → 503, cancellation → 499. Clients:
one method each in `clients/typescript` (binary response via `arrayBuffer`)
and `clients/dotnet`; TS wire types regenerate from OpenAPI.

**Seams for sibling plans** (not built here):
`map-service-plan.md` M2 maps `export`/`tile` onto these routes;
`image-service-plan.md` I2 uses `IRasterOperations.ReadAsync` for
`exportImage`. Both are later, separately-gated changes.

## 7. Phases

### R0 — Contracts, guards, decision (small)

- **Deliverable:** ADR-0044 accepted; `Spatial.PluginSdk.Rendering` contracts
  + HTTP DTOs; `Directory.Packages.props` versions; architecture-guard
  entries (`PlatformProjectNames`, `ImplementationProjectNames`,
  `AllowedPackages`, `Host_references_only_expected_projects`); `slnx` and
  empty projects; DI registration stubs.
- **Proof:** architecture tests green with the new projects; SDK still
  references Core only and takes no packages; no implementation type visible
  from the SDK.

### R1 — Vector render, no imagery

- **Deliverable:** `Spatial.Rendering.Skia` renders `fill`/`line`/`circle`
  from a minimal style document; PNG encode via Skia; `POST /api/render`;
  `.NET` + TS client methods.
- **Proof:** golden-image tests at fixed extent/size; determinism (same input
  → identical bytes on the CI image); success/`invalid.arguments`/cancellation
  paths; store `not.found`.

### R2 — Imagery and composition

- **Deliverable:** `Spatial.Imagery.Vips` implements `IRasterOperations`
  (`ReadAsync`, `CompositeAsync`); the renderer composes over configured
  imagery; PNG/JPEG/WebP encode.
- **Proof:** regression tests for the spike's traps — sRGB interpretation
  before `composite2`, equal band counts, premultiplied `Over`, stride
  handling; a golden composite; container test that native libs ship.

### R3 — Style document and authoring

- **Deliverable:** MapLibre-subset JSON parser + `DrawPlan` compiler cached
  by style hash; zoom windows; attribute filters lowered onto the safe
  grammar; optional CSS-ish authoring via ExCSS.
- **Proof:** style fixtures (property resolution tables as pure unit tests);
  unsupported property → typed error; workbench style document round-trips
  the subset it uses.

### R4 — Tiles and cache

- **Implemented** per ADR-0046. `TileCoordinate` + `ITileScheme` (pluggable
  projections) in the SDK; `Spatial.Tiling.WebMercator` is the first scheme;
  `ITileCache` with a content-addressed `TileCacheKey`, the initial
  `InMemoryTileCache` (LRU, byte + entry bounds) and `TileService` (single +
  bounded-parallel batch) in the host; the tile routes above.
- **Deliverable:** `TileCoordinate`, Web-Mercator LOD math, parallel tile
  batch, content-addressed cache with eviction, tile route.
- **Proof (built):** LOD math against the spec's tiling scheme (unit); tile
  determinism; cache hit/miss/eviction/invalidation; bounded-parallelism and
  cancellation. The plan's `GET` tile route was revised to `POST` because the
  style/layer document needs a body (ADR-0046).

### R5 — Publication and GeoServices seam (gated)

- **Deliverable:** resolve `PublicationKind.Map` via `IPublicationRegistry`;
  GeoServices `export` + `tile` mount per `map-service-plan.md` M2/M3.
- **Partly implemented:** the neutral host route
  `POST /api/publications/{name}/render` resolves a publication and renders
  its datasets with the persisted per-layer style (ADR-0047); the GeoServices
  `export`/`tile` projection is implemented as the MapServer (ADR-0048).
- **Proof:** spec response shapes; capability strings match what is served;
  golden export at fixed extent/dpi.

### R6 — Labels and symbols

- **Deliverable:** `SkiaSharp.HarfBuzz` shaping over the embedded Noto Sans
  Regular 2.003 (OFL-1.1), a deterministic placement/collision pass, and
  `Svg.Skia` sprite icons from the embedded sprite registry (plus the bundled
  `default-marker`). New packages pinned in `Directory.Packages.props` and
  allowlisted by ADR-0049.
- **Proof (built):** property-resolution and typed-rejection unit tests;
  anchor/offset/halo/collision fixtures; byte-identical repeated runs and
  feature-order independence; the embedded-font hash and sprite registry;
  `POST /api/render` and `POST /api/render/tiles/{z}/{x}/{y}.{format}` symbol
  coverage; a committed golden render (byte-equal on CI, bounded tolerance
  elsewhere).

### R7 — Performance / GPU (only with evidence)

- **Deliverable:** none until a measured bottleneck and an ADR justify a GPU
  backend (Skia Ganesh/Graphite) or a second renderer implementation.

## 8. Work packages

| # | Package | Depends on | Proof |
| --- | --- | --- | --- |
| 1 | ADR-0044 + contracts + guard entries | — | architecture tests |
| 2 | Project skeletons + DI registration | 1 | host boots; `/api/render/capabilities` |
| 3 | Viewport/projection + `FeaturePipeline` | 2 | unit (projection, bbox query) |
| 4 | `StyleCompiler` (fill/line/circle) | 2 | resolution-table unit tests |
| 5 | `SkiaVectorRasterizer` + PNG encode | 3, 4 | golden images |
| 6 | `POST /api/render` + clients | 5 | HTTP + e2e |
| 7 | `Spatial.Imagery.Vips` `ReadAsync`/`CompositeAsync` | 1 | trap regression + golden composite |
| 8 | Renderer composes over imagery | 6, 7 | golden composite over fixture |
| 9 | MapLibre-subset document + CSS authoring | 4 | style fixtures |
| 10 | Tiles + LOD + cache | 8 | determinism + cache tests |
| 11 | Publication resolution + GeoServices export seam | 10 | spec fixtures (gated) |
| 12 | Labels/symbols | 9 | placement fixtures |
| 13 | Docs, distilled route, ADR register, `eng/verify.sh` | all | green verify |

Packages 1–6 are the first useful vertical slice (vector-only export) and
ship independently. Package 7 is where the native imagery dependency lands.

## 9. Testing strategy

- **Unit (implementation projects):** projection/LOD math, style resolution
  tables, geometry→path conversion, blend/format mapping, error mapping.
- **Golden images:** fixed viewport, fixed features, fixed style, **bundled
  font**, AA pinned. Compare file bytes on the CI Linux image; on other
  platforms compare with a small per-pixel delta and a bounded mismatch
  ratio. Goldens live under `tests/fixtures/rendering/golden/`.
- **Architecture:** SDK has no renderer packages; Skia types only in the
  Skia project, NetVips types only in the Vips project; no
  cross-implementation references; package allowlist entries present.
- **Host HTTP:** content type/format, error mapping, pixel cap, cancellation.
- **End-to-end:** `eng/e2e-web.sh` calls the render route from the TS SDK;
  `eng/workbench-e2e.sh` gains an export action when the workbench surfaces
  it.
- **Quality loop:** after R1 and R2, run warnings/metrics/CRAP/coverage. The
  rasterizer will be split (`SkiaVectorRasterizer`, paint factory, path
  builder) rather than grandfathered; the coverage floor is 70% branches.

## 10. Performance budget (from the spike)

Measured on 24 cores, 512², Release (`research/rendering/README.md`):

| Case | vertices | raster | compose | pipe/s |
| --- | ---: | ---: | ---: | ---: |
| light | 1,600 | 9 ms | 10 ms | 48.7 |
| medium | 32,000 | 145 ms | 40 ms | 4.4 |
| heavy | 128,000 | 651 ms | 21 ms | 1.4 |

Parallel tile batch: **65 tiles/s** end-to-end for the medium case (~13×);
dashed strokes cost **1.49×** solid. Targets to defend in R1–R4:

- vector-only 512² demo render p95 ≤ **50 ms** after warmup;
- 1024×576 with imagery p95 ≤ **150 ms**;
- tile batch ≥ **50 tiles/s** on 24 cores for the demo dataset;
- no full-image copy beyond the RGBA buffer + the libvips lazy chain.

Levers in order: simplify once per zoom and reuse, bbox pushdown, drop dashes
on dense layers, parallel tiles, NetVips lazy chain, then (only then) GPU.

## 11. Security, limits, failure modes

| Concern | Decision |
| --- | --- |
| SSRF | imagery `Source` is a **configured key/path**, never a request URL |
| Style isolation | style is data: no code, no SQL; filters use the safe grammar |
| Resource caps | pixel count, layer count, tiles-per-batch, request timeout |
| Cancellation | `CancellationToken` flows from the aborted request into store + raster |
| Secrets | none; imagery paths come from host config only |
| Diagnostics | `SpatialException` codes `invalid.arguments` / `not.found` / `store.unavailable`; no paths or secrets in messages |
| Native surface | libvips (LGPL-2.1, dynamic via `NetVips.Native`) and Skia natives pinned; container size is a tracked cost |

## 12. Exit criteria / definition of done

Per the repo definition of done (typed contracts, success/failure/cancellation
tested, no prohibited dependency, actionable diagnostics, docs/ADRs updated,
clean `eng/verify.sh`). R1–R2 are done when:

- a client can `POST /api/render` a MapLibre-subset style over demo or PostGIS
  datasets and receive a deterministic PNG, with imagery composited when
  configured;
- the spike's libvips traps have regression tests, and the native libraries
  ship in the container image;
- architecture guards pin the two new projects and their packages;
- the quality-loop gates (warnings, metrics, CRAP, coverage) are green.

## 13. Risks

| Risk | Mitigation |
| --- | --- |
| Golden images brittle across Skia/libvips versions/platforms | bundled font, pinned AA, byte-equality only on the CI image, tolerance elsewhere |
| Style-spec scope creep | documented subset; unsupported → typed error; no parity claim |
| Native dependency / image size / LGPL | `NetVips.Native` shared lib, pinned versions, size budget in CI |
| Metrics/CRAP on a large rasterizer | design the cohesive splits up front (ADR-0040) |
| Label collision is the hard part | isolated in R6 with its own fixtures; not smuggled into R1 |
| Cache invalidation | key on style hash + dataset version; explicit invalidation tests in R4 |
| Renderer becomes a second spatial engine | read/shape/place call the existing services only; no algorithms in the renderer |
| Premature GPU/AOT detour | R7 requires measured demand + ADR (ADR-0012/0021) |

## 14. Traceability and open questions

Implements ADR-0044 under ADR-0033; respects ADR-0001/0005/0009/0012/0020/0021/
0040. Consumed by `map-service-plan.md` (M2/M3) and
`image-service-plan.md` (I0/I2). Evidence: `research/rendering/`.

Open questions to settle at implementation time:

- **Store resolution shape:** request carries resolved `IFeatureStore` +
  `IDataCatalogue` (proposed, testable) versus passing `IServiceProvider`
  (adapter style). Decide in R1 and record it.
- **Cache ownership:** settled in ADR-0046 — the interface owns the decision;
  the first implementation is a host-memory LRU cache, and a filesystem /
  object-store / PostGIS implementation is another registration (shared with
  the map-service plan M3).
- **Style storage:** inline request body versus named styles from
  `Spatial:Rendering:StyleRoot` (leaning: both, named preferred). Still open;
  the tile route carries the document in the body until named styles land.
- **Imagery breadth:** NetVips covers COG/GeoTIFF read and warp; the
  GDAL-vs-managed decision belongs to the image-service plan I0.
- **Format set:** which of WebP/TIFF/JPEG the host advertises, gated on the
  built libvips.
