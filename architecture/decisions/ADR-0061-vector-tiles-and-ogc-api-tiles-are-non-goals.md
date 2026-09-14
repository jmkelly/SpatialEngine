---
status: accepted
date: 2026-09-14
deciders: maintainer + agent
---

# ADR-0061: Vector tiles and OGC API Tiles are documented non-goals; raster tiles stay the surface

## Context

`research/compat/tiles.md` leaves two tile rows undecided (T-M follow-ups,
owned by T-048 per the ADR-0059 scope split):

1. **Vector tiles** (S2 `.vtpk`, MVT endpoints): the engine renders raster
   tiles only. There is no MVT encoder anywhere in tree, no
   `VectorTileServer` route, and a `.vtpk` package would need the
   packaging-plus-job model that ADR-0033 deleted and ADR-0059 closed
   (`exportTiles` already rejects by name, including its `.vtpk` variant).
2. **OGC API Tiles** (S5, the modern path): no TileJSON, no tiles landing
   page, no `/collections/{id}/tiles` JSON surface exists in tree. The OGC
   adapter serves WMS/WFS only; the neutral tile routes carry a render
   document in the POST body, not an OGC tile-set resource.

The standing constraints are ADR-0044 (the raster rendering pipeline:
vector data in, styled raster out — no vector-output stage) and ADR-0033
(long-running work is a cancellable `Task`; no job model, so no offline
package to serve or describe). Serving either item honestly would mean a
new MVT encoding stage **or** a new OGC tile-set resource model plus, for
`.vtpk`, the already-rejected packaging pipeline — new architecture, not a
new route, with no requesting client trace in tree.

## Decision

**Both items are documented non-goals. No MVT encoder, no
`VectorTileServer`, no `.vtpk` packaging and no OGC API Tiles JSON are
built. Raster tiles stay the only tile surface: the neutral
`POST /api/render/tiles/...` + `GET /api/maps/{name}/tiles/...` routes and
the MapServer `tile/{z}/{y}/{x}` seam over the pluggable `ITileScheme`
(ADR-0046), with the LOD grid proven against the G1 cached root (T-048).**

Unlike the ADR-0059 Esri operations — real `MapServer`/`ImageServer`
operation URLs that clients probe, hence rejected by name — there is no
mounted-but-unserved operation path here: no `MapServer` or `ImageServer`
operation names vector tiles (the `exportTiles` `.vtpk` variant already
rejects by name under ADR-0059), and no OGC tile-set route is mounted, so
those shapes stay framework 404 and the absence is documented in the
compatibility matrix instead of with reject routes. A capabilities crawler
looking for `VectorTileServer` or `/ogc/.../tiles` finds nothing mounted,
which is the honest shape for an unserved server type.

If measured client demand ever reopens either item, that is a new task
with its own ADR — an MVT stage must reconcile with ADR-0044 first, and
`.vtpk` packaging must reconcile with ADR-0033 first.

## Consequences

- A vector-tile client (MapLibre GL with an MVT source, an ArcGIS
  `VectorTileServer` workflow) finds no vector endpoint and no package to
  download; the matrix tells it the engine serves pre-styled raster tiles,
  so it consumes `tile/{z}/{y}/{x}` or the neutral tile routes instead.
- An OGC API Tiles client finds WMS/WFS but no `/tiles` landing page; the
  matrix records the gap rather than a half-served TileJSON that names
  tile sets the host does not implement.
- T-048 closes with LOD proof + rejects + this decision: the tiles matrix
  has no open rows.

## Alternatives

- **Encode MVT from the feature stores:** would serve real vector tiles,
  but invents a vector-output stage the raster pipeline (ADR-0044) has no
  room for — encoders, per-layer tile schemas, font/glyph serving — with
  no requesting client trace. Rejected.
- **Serve a static TileJSON over the live raster scheme:** cheap to write,
  but TileJSON advertises vector source-layers the host cannot produce,
  so conformant clients would break on first fetch. Rejected; a
  documented absence beats a half-served standard (same reasoning as
  ADR-0059's WMTS alternative).
- **Mount named rejects for `/VectorTileServer` and `/ogc/.../tiles`:**
  would name the gap loudly, but those server types are never mounted, so
  there is no operation URL a client of *this* host could have formed —
  the matrix row is the honest record, not a route. Rejected.
