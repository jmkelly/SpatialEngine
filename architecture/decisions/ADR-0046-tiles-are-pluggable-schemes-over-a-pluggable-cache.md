---
status: accepted
date: 2026-09-15
deciders: maintainer + agent
---

# ADR-0046: Tiles use pluggable schemes over a pluggable cache

## Context

ADR-0044/`rendering-implementation-plan.md` R4 adds server-side map tiles on
top of the R0–R3 raster pipeline. The plan left two questions open and
implicitly assumed one shape:

- **Projections.** The renderer already computes Web-Mercator zoom for style
  windows, but there is no tile address model and no mapping from a tile
  address to a projected extent. The maintainer requires the tiling scheme to
  be pluggable so a second projection can arrive later without touching the
  renderer.
- **Cache ownership.** `map-service-plan.md` §6 flags that
  `singleFusedMapCache` implies a cache lifecycle the engine "has no home
  for" and asks for host-filesystem vs object-store/PostGIS to be decided
  before tiles ship. The maintainer's direction is that the *interface* is the
  durable decision and the first implementation is an in-memory cache with a
  configurable maximum size that evicts the oldest tiles; ownership follows
  the implementation.
- **Request shape.** A tile has no viewport in the request: the scheme derives
  it from the address. The tile document (style, layers, imagery) still has to
  travel somewhere.

Standing constraints apply unchanged: contracts are core-typed in
`Spatial.PluginSdk` (ADR-0005/0033), algorithms live in implementation
projects (principles 1/7), each implementation is a separate project composed
by DI, and new code clears the metrics/CRAP/coverage gates (ADR-0040).

## Decision

**1. `ITileScheme` is the pluggable projection contract.**

`Spatial.PluginSdk` owns the core-typed values `TileCoordinate(z, x, y)` and
`TileLevel(zoom, resolution, scaleDenominator)` and the `ITileScheme`
interface (`Id`, `Crs`, `TileSize`, `MinZoom`, `MaxZoom`, `Levels`,
`IsValid`, `Bounds`, `Resolution`). The first implementation,
`WebMercatorTileScheme` (the standard EPSG:3857 XYZ scheme, 256 px, top-left
origin, zoom 0–23), lives in its own `Spatial.Tiling.WebMercator` project. A
second projection is a sibling implementation of the same contract; the Host
registers every `ITileScheme` and resolves one by id (default
`Spatial:Tiles:DefaultScheme`), so the renderer, routes and cache never change
for a new projection. Projection and level-of-detail math is the scheme's
only responsibility and never enters the renderer.

**2. `ITileCache` is the pluggable ownership contract.**

`Spatial.PluginSdk` owns `TileCacheKey(Scheme, Z, X, Y, Format, Version)` and
`ITileCache` (`TryGetAsync`, `SetAsync`, `RemoveAsync`, `ClearAsync`). The
key is content-addressed: `Version` is a SHA-256 over the style document, the
ordered layer/store/filter descriptors, the imagery stack and the encoding
options, so a style or dataset-reference change produces a different key with
no scan (a store-level version stamp can replace the descriptor-derived
version when stores expose one). The **initial implementation is
`InMemoryTileCache` in `Spatial.Host`**, bounded by a configurable total byte
budget and entry count (`Spatial:Tiles:Cache`), evicting the
least-recently-used tile first and refusing tiles larger than the budget; a
non-positive bound disables caching. Ownership (host filesystem, object
store, PostGIS) is deliberately deferred: a different cache is another
`ITileCache` registration and changes neither the routes nor the renderer.

**3. Tile orchestration is a Host service, not a renderer method.**

`TileService` in `Spatial.Host` resolves the scheme, checks the cache, derives
the tile viewport from `ITileScheme.Bounds`, renders through the resolved
`IMapRenderer` and stores the image. A batch renders an ordered tile list with
`Parallel.ForEachAsync` bounded by `Spatial:Tiles:Concurrency` (default: the
processor count) and a `MaxTilesPerBatch` cap, preserving request order in
the response. Cancellation flows from the request into the renderer.

**4. The tile routes carry the document; the path carries the format.**

The routes are `POST /api/render/tiles/{z}/{x}/{y}.{format}` (one binary
tile, `X-Tile-Cached` reports the cache disposition), `POST
/api/render/tiles/batch` (an ordered JSON list with Base64 content), `GET
/api/render/tiles/capabilities` (registered schemes, LODs, batch cap) and
`DELETE /api/render/cache` (explicit invalidation). This is a deliberate
revision of the plan's `GET /api/render/tiles/{z}/{x}/{y}.{format}`: a
bodyless GET cannot carry the style and layer document without a server-side
named-style registry, which remains an open item below. The path format is
authoritative over the body's `format`.

## Consequences

- The engine gains tiles while keeping its walls: no projection type in the
  renderer, no cache type in the contracts beyond the interface, and no
  algorithm in `Spatial.Core`.
- New architecture-test allowlist entries are required for one implementation
  project (`Spatial.Tiling.WebMercator`, no packages) and the Host's reference
  to it. The in-memory cache adds no project.
- A world-covering tile (zoom 0) exposed a latent bug in the render pipeline:
  the viewport-bbox pushdown transformed bounds in the wrong direction
  (`GeometryPipeline.TransformEnvelope` passed the dataset CRS as the source),
  which only surfaced when the viewport CRS differed from the dataset CRS.
  The direction is fixed and pinned by a unit test; geographic datasets now
  query correctly under Web-Mercator tile viewports.
- Cache invalidation on *data* change needs a dataset version; until stores
  expose one, operators invalidate with `DELETE /api/render/cache` and the
  version folds the style and dataset references.
- The in-memory cache is per-process; a multi-instance deployment gets no
  sharing until a filesystem/object-store implementation lands. That is the
  point of keeping ownership behind `ITileCache`.
- The metrics `architectural-rigidity` rule flags `Spatial.PluginSdk.Http`
  (abstractness 0, D 0.60): adding the tile HTTP DTOs raises the namespace's
  afferent coupling past the zone-of-pain line. This is inherent to adding
  routes that bind HTTP DTOs, not to the tile design; the remediation is an
  abstraction in the DTO namespace or a DTO-namespace refactor, deliberately
  not smuggled into R4. The gate was already red on `Spatial.ClientTransport`
  `low-cohesion` before this change, so R4 does not make it green either way.
- The GeoServices `tile/{z}/{y}/{x}` seam (R5) maps onto `TileService`; it is
  not built here, and `singleFusedMapCache` is still not claimed.

## Implementation status

Implemented (R4 of `rendering-implementation-plan.md`): the
`Spatial.PluginSdk` tile values and `ITileScheme`/`ITileCache` contracts and
HTTP DTOs; `Spatial.Tiling.WebMercator`; the Host's `InMemoryTileCache`,
`TileFingerprint`, `TileService`, routes and `Spatial:Tiles` configuration;
and the `.NET` `SpatialClient.Tiles.RenderAsync`/`CapabilitiesAsync` and
TypeScript `renderTile`/`renderTiles`/`tileCapabilities` clients. T-001 adds
the second `ITileCache`: the Host's `FileTileCache`, selected by
`Spatial:Tiles:Cache:Provider: file` with a shared `Root` directory, so
tiles survive a host restart and are shared between hosts on the same
root (eviction stays LRU within the same byte/entry bounds; storage failures
are `store.unavailable`). No new seam, package or contract: this fills the
deferred-ownership slot above, so it amends this ADR in place rather than
taking a new number. Not implemented: the GeoServices `export`/`tile` seam
(R5), labels/symbols (R6), a GPU backend (R7).

## Alternatives

- **Projection math inside `Spatial.Rendering.Skia`** — fewer projects, but the
  renderer would own projection concerns and a second projection would edit
  it. Rejected: pluggability is the requirement.
- **A single `Spatial.Tiling` project with the cache** — mixes pure math with
  storage ownership and forces a rebuild of the scheme when the cache changes.
  Rejected for cohesion (ADR-0040).
- **A filesystem cache first** (`CacheRoot`, as the plan sketched) — pre-empts
  the ownership decision the maintainer explicitly left open and adds paths,
  durability and eviction semantics before they are needed. The interface is
  ready for it; the default is memory.
- **FIFO eviction** — simplest, but a map client revisits recent tiles far more
  than it revisits the oldest; LRU is the same complexity and serves the
  access pattern. "Oldest" is interpreted as least-recently-used and is
  documented on the implementation.
- **`GET` tile route with a query-encoded style** — unusable for a MapLibre
  document and leaks encoding concerns; a server-side named-style registry is
  the right home and is deferred.
