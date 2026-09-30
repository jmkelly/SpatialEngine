---
status: accepted
date: 2026-09-16
deciders: maintainer + agent
summary: Vector tiles (MVT) and OGC API Tiles are in scope; live MVT, TileJSON and collection tile resources are implemented; `.vtpk` packaging still needs its own ADR.
supersedes: ADR-0062
---

# ADR-0070: Vector tiles and OGC API Tiles are in scope (ADR-0062 superseded)

## Context

ADR-0062 documented Mapbox Vector Tiles (MVT), the Esri `VectorTileServer`
shape (including the `.vtpk` packaging variant) and OGC API Tiles as
non-goals, with raster tiles as the only tile surface. The reasoning then
was sound for the scope at the time: no MVT encoder in tree, no tile-set
resource model, and no requesting client trace — plus the `.vtpk`
packaging-plus-job model conflicted with ADR-0033 (long-running work is a
cancellable `Task`, no job model) and a vector-output stage had no room in
the ADR-0044 raster pipeline.

Since then the product direction has narrowed to one wedge: **PostGIS as
Esri REST in one container**. Modern web clients for that wedge —
MapLibre GL, OpenLayers, ArcGIS Maps SDK — expect vector tiles first and
raster `exportImage`/`tile` second. Staying raster-only concedes the
default frontend path and forces every browser client through the hardest
part of our stack to scale (server render per tile, no shared cache).
The restriction has become a go-to-market liability, not a scope guard.

## Decision

**ADR-0062 is superseded. MVT vector tiles and OGC API Tiles are in
scope as future work. Raster tiles stay fully supported; they are joined
by, not replaced by, a vector surface.**

Concretely:

1. **MVT encoding is a new pipeline stage, not a renderer tweak.**
   ADR-0044 gains a vector-output sibling: feature stores → per-layer
   tile schema → MVT encoder → bytes. Encoders, layer schemas and any
   font/glyph serving live in their own implementation project(s) behind
   core-typed contracts; third-party encoding types never cross
   `Spatial.Contracts` (principle 5 / ADR-0005, unchanged).
2. **Two serving shapes, in order:**
   - (a) Neutral + Esri: `GET /api/maps/{name}/tiles/mvt/{z}/{x}/{y}.pbf`
     (or equivalent) alongside the existing raster tile routes, projected
     as `VectorTileServer` under `MapServer`/`service` where the Esri spec
     expects it.
   - (b) OGC API Tiles: TileJSON + tiles landing page +
     `/collections/{id}/tiles` JSON surface in `Spatial.Adapter.Ogc`,
     reusing the same scheme/cache contracts.
3. **ADR-0033 still holds.** Live tiles are cancellable `Task`s. Offline
   `.vtpk` packaging stays a non-goal until a packaging-plus-progress
   model is designed in its own ADR; `exportTiles` keeps rejecting by
   name under ADR-0060.
4. **ADR-0046 still holds.** The pluggable `ITileScheme`/`ITileCache`
   contracts extend to vector bytes; Web-Mercator XYZ is still the first
   scheme. A persistent/shared cache (already "not done" for raster)
   becomes more urgent once vector tiles land and is tracked with the
   tile work, not separately.

## Consequences

- The compatibility matrix (`research/compat`, `architecture/references/
  geoservices-compatibility.md`) regains open rows for MVT / OGC API
  Tiles instead of documenting absence; a capabilities crawler will
  eventually find mounted routes rather than framework 404s.
- New contract surface is required (tile schema, vector-tile service
  faces) with the usual contract → SDK → test → ADR bundle per behaviour
  change; the TypeScript and .NET SDKs gain tile-fetch helpers with
  drift-checked wire types.
- T-002 implements phase A: `Spatial.Contracts` adds the core-typed
  `IVectorTileService` request/result seam; `Spatial.Tiling.Mvt` implements
  MVT 2.1 encoding over resolved feature stores; the host serves
  `GET /api/maps/{name}/tiles/mvt/{z}/{x}/{y}.pbf`; and the GeoServices
  adapter projects the same cache/scheme/service seam at
  `.../MapServer/vectorTile/...` and `.../VectorTileServer/tile/...`.
  Raster remains fully supported. OGC API Tiles are implemented in T-003 as
  map-scoped landing/collections resources, TileJSON and negotiated MVT tile
  data over the same scheme/cache/service seam. `.vtpk` packaging and
  `exportTiles` remain out of scope.

## Alternatives

- **Keep ADR-0062 and stay raster-only:** rejected — concedes MapLibre /
  OpenLayers / ArcGIS SDK default paths and leaves the scaling burden on
  per-request server rendering.
- **Serve static TileJSON over the raster scheme now:** rejected (same
  reasoning as ADR-0062) — TileJSON advertises vector source-layers the
  host cannot produce; a documented future beats a half-served standard.
- **Full `.vtpk` offline packaging in the same ADR:** rejected — needs a
  job/packaging model ADR-0033 deliberately removed; kept as a separate
  future decision.
