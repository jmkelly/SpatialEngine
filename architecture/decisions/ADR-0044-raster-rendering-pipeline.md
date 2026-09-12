---
status: proposed
date: 2026-09-14
deciders: maintainer + agent
---

# ADR-0044: Raster rendering is a pipeline over Skia (vector) and NetVips (imagery)

## Context

The engine is headless and the browser workbench renders client-side, so
"no map rendering, tiling or export" is currently an explicit non-goal of the
GeoServices work (`architecture/geoservices-implementation-plan.md` §Non-goals;
ADR-0035). Server-side raster output is now wanted: take the engine's vector
outputs, apply a style, and produce tiles or a one-off raster for export,
with **NetVips as the imagery layer** (the maintainer's stated requirement).

Numbering: ADR-0041 is the ingest/publications decision and ADR-0042/0043 are
reserved by `publishing-and-ingest-plan.md`, so this decision takes 0044.
The execution plan is `architecture/rendering-implementation-plan.md`.

Three standing decisions constrain how this can be built:

- ADR-0005 / principle 8: third-party types (NTS, Npgsql, EF, **renderer**)
  never cross a public contract. The renderer's types must stay inside the
  owning implementation.
- ADR-0033 / principle 7: services are in-process interfaces in
  `Spatial.PluginSdk`, composed by DI; implementations depend on contracts,
  **never on each other**.
- ADR-0040: new code must clear the metrics/CRAP/coverage gates.

The research report (`research/rendering/README.md`) probes the candidate
field, filters it on those walls, and spikes the survivor. Findings:

- **SkiaSharp** (MIT, 330M+ downloads, active; v4 moving to `SKPathBuilder`)
  is the only mature, permissively-licensed, first-party .NET path
  rasterizer. Every alternative fails a wall: MapLibre Native and Vello have
  no maintained .NET binding; the Mapnik binding is stale; ImageSharp.Drawing
  is under a non-permissive split licence; Magick.NET is a raster toolkit,
  not a path rasterizer; SharpMap is unmaintained.
- **NetVips** (MIT binding over libvips 8.18.x) is the requested imagery
  pipeline and takes a raw RGBA buffer straight from Skia.
- The style dialect the delivered client already speaks is the **MapLibre
  style spec** (`apps/workbench-web/src/screens/MapScreen.tsx`). Sharing that
  document is the cheapest route to client/server visual agreement.
- The spike renders real `Spatial.Core` geometry through
  `ICoordinateTransforms` and `IGeometryOperations.Simplify` into Skia,
  composites over NetVips imagery and encodes; it measured ~13× parallel
  scaling on 24 cores and 1.49× dash-vs-solid stroke cost.

## Decision

Raster rendering is a **composed pipeline**, not a new subsystem bolted on:

1. **Two SDK contracts, core-typed** (`Spatial.PluginSdk`):
   - `IMapRenderer.RenderAsync(MapRenderRequest) -> RasterImage` — the whole
     pipeline (read → shape → place → raster → compose → encode).
   - `IRasterOperations.CompositeAsync(RasterComposite) -> RasterImage` —
     imagery verbs (load, resize, blend, encode) over raw pixel buffers.
   - DTOs carry only core/framework types: `ReadOnlyMemory<byte>` pixels,
     `Envelope`-shaped viewports, enums. No Skia, no NetVips (ADR-0005).

2. **Two implementation projects**, each owning its native dependency:
   - `Spatial.Rendering.Skia` implements `IMapRenderer` and contains every
     SkiaSharp type. It depends on the *contracts* `IFeatureStore` /
     `IDataCatalogue` / `ICoordinateTransforms` / `IGeometryOperations` /
     `IRasterOperations`, injected by DI.
   - `Spatial.Imagery.Vips` implements `IRasterOperations` and contains every
     NetVips type.
   - Neither implementation references the other; `Spatial.Host` wires them.

3. **Style document is the MapLibre style spec** (a documented subset:
   `background`/`fill`/`line`/`circle` first, `symbol` later). A CSS-ish
   authoring layer may lower onto the same internal compiled style model;
   the toy parser in the spike is not the production parser.

4. **Performance shape**: bbox pushdown via the existing
   `IFeatureStore.QueryAsync(bbox)`, per-zoom simplification reused across
   tiles, independent stateless tiles parallelised across cores, and NetVips'
   lazy chain for imagery so the vector buffer is never round-tripped.

5. **GeoServices `export` / tile endpoints are a separate later decision.**
   This ADR adds the capability and a host route; it does not, by itself,
   claim `MapServer` conformance.

## Consequences

- The engine gains a renderer while keeping its walls: rendering algorithms
  and native types live in an implementation, contracts stay core-typed, and
  composition stays in the Host.
- New architecture-test allowlist entries are required (two projects, their
  packages: SkiaSharp + native assets + HarfBuzz later, NetVips + native).
  Both are added deliberately in the same change, not silently.
- The host stays JIT (ADR-0012); SkiaSharp/NetVips native interop is not an
  AOT path, so ADR-0021 is untouched.
- libvips is LGPL-2.1 and is consumed as a shared library via
  `NetVips.Native`, satisfying dynamic-linking obligations.
- `Spatial.Host` now optionally depends on a renderer; a deployment without
  imagery can run the vector-only path (phase 2) before the Vips project
  lands.
- Scope is fixed and honest: label placement/collision, full style-spec
  coverage, tile caching and GPU back ends are **not** in this decision.

## Alternatives

- **MapLibre Native** — best style parity, BSD-2, active; rejected because
  there is no maintained .NET binding (C++/FFI or Node sidecar would fight
  ADR-0033). Revisit only if parity outranks everything and gets its own ADR.
- **Vello** — genuinely modern GPU vector renderer; rejected for the same
  binding reason. Watch it.
- **ImageSharp.Drawing** — managed and capable; rejected on the Six Labors
  Split Licence in an MIT-first repository.
- **Magick.NET** — Apache-2.0, but a raster/augmentation toolkit; rejected as
  the path rasterizer (it remains a possible *imagery* backend, but NetVips
  is the chosen one).
- **Mapsui / SharpMap** — map components already sitting on SkiaSharp (or
  stale); rejected because their layer models would fight the engine's
  service model.
- **A single project owning both Skia and NetVips** — simpler, but the
  maintainer explicitly wants NetVips as the imagery service, and two
  interfaces keep the imagery verbs independently replaceable. If the split
  proves to cost cohesion, fold them with a follow-up ADR.
