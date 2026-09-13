---
status: accepted
date: 2026-09-15
deciders: maintainer + agent
---

# ADR-0048: MapServer is a projection of a Map publication over the render and tile contracts

## Context

`architecture/map-service-plan.md` scoped a GeoServices MapServer (spec §4)
on the engine. Its **blocking decision** was rendering: a MapServer is only
useful to ArcGIS clients if it can serve `export` and tiles, and the engine
was headless. That blocker is now cleared:

- ADR-0044 built the raster pipeline (`IMapRenderer`, `IRasterOperations`);
- ADR-0046 built tiles (`ITileScheme`, `ITileCache`);
- ADR-0047 persisted per-layer MapLibre style on a `Publication`.

So M0–M4 of the plan are unblocked. Three design questions remain and are
settled here, because they are architectural rather than mechanical:

1. **How does the adapter reach rendering/tiling?** The GeoServices adapter
   (`Spatial.Adapter.GeoServices`) may reference only `Spatial.Core`,
   `Spatial.PluginSdk` and the two interop codecs (ADR-0033/0035), so it
   cannot reference `Spatial.Host`'s `TileService` or
   `Spatial.Rendering.Skia`. The render and tile **contracts** are already in
   the SDK, registered in DI by the host, so the adapter consumes them
   directly.
2. **Style assembly is shared.** A publication's per-layer fragments become a
   MapLibre document by one rule (ADR-0047: inject `id`/`source-layer`,
   default a style-less layer). The host and the adapter must not diverge, so
   the rule moves to a dependency-free SDK helper, `PublicationMapStyle`.
3. **Map-specific metadata** (extents, units, `tileInfo`, `drawingInfo`) has
   no engine home yet and is projected at the adapter edge.

## Decision

**A MapServer is a projection of a `PublicationKind.Map` publication** (or a
declared publication of that kind), served by the GeoServices adapter over
the SDK render/tile contracts.

1. **Routes** (spec §4): `/{service}/MapServer` (root), `.../{layerId}`
   (layer), `.../{layerId}/query` (reuse the FeatureServer query engine),
   `.../layers` (all layers and tables), `.../identify`, `.../find`,
   `.../export`, `.../tile/{z}/{y}/{x}`, and `.../tileInfo`. Every route
   negotiates `f=json` except `export`, which streams `f=image` bytes or a
   JSON `{href}` (spec §4.0.4). The catalog advertises Map publications as
   `MapServer`.
2. **Rendering is the SDK contracts.** Export resolves the publication's
   layers to `MapLayerSource`, composes the style with
   `PublicationMapStyle`, and calls `IMapRenderer`. Tiles do the same and
   go through `ITileScheme` + `ITileCache` (the cache version is a hash of
   the service and its style). No new dependency, no host coupling.
3. **`layerDefs` and `layers=show|hide`.** `layers` selects publication
   layers by stable id; `layerDefs` is parsed with the shared
   `EsriFilterClause` grammar and pushed down as each `MapLayerSource.Filter`
   (ADR-0028's parameterised filter), never as raw SQL. The style fragment
   is unaffected.
4. **Extents and units.** `initialExtent`/`fullExtent` are computed by
   scanning the service's layers and unioning geometry envelopes (there is
   no store extent verb). `units` maps the map SRID to the Esri units
   constant; the map SR is the first layer's SRID. A dataset-heavy service
   pays a scan per root request — accepted for now, with a store extent
   capability recorded as the future fix.
5. **Tiles.** `singleFusedMapCache` is `true` and `tileInfo` is projected
   from the registered Web-Mercator `ITileScheme` LODs; uncached tiles are
   rendered on demand and cached (ADR-0046).
6. **`drawingInfo` (M4).** Each layer's persisted style maps to an Esri
   `simple` renderer: a `fill` fragment → `esriSFS`, a `line` → `esriSLS`, a
   `circle` → `esriSMS`; colours are converted to Esri RGBA. A publication
   layer with no style carries no `drawingInfo`. Richer Esri renderer types
   (class breaks, unique value, labels) are **not** produced; the renderer's
   MapLibre subset is the source of truth (ADR-0044).
7. **Capabilities.** The root advertises `"Map,Query,Data"` because
   `export` and tiles are served. The capability string is tested against
   what is actually mounted.

Read-only throughout (spec §4.0); the standing non-goals hold (no
`queryRelatedRecords`, attachments, `htmlPopup`, time, versioning,
editing).

## Consequences

- The GeoServices facade now serves Map, Feature and Geometry services from
  one route group; the catalog advertises each publication by kind.
- The adapter gains Map metadata shaping, identify/find engines and a thin
  render/tile bridge — all protocol mapping, no spatial algorithm (the
  adapter keeps delegating geometry work to `IGeometryOperations` /
  `ICoordinatesTransforms` / the stores).
- A publication with no persisted style renders with the neutral default
  symbol (ADR-0047), so an unstyled Map publication is still visible.
- Extents are a scan; a per-dataset `IDatasetExtent` capability is the
  recorded follow-up, not part of this decision.
- The `Spatial.PluginSdk` gains one pure, framework-only helper
  (`PublicationMapStyle`); it stays core-typed and package-free.

## Alternatives

- **Mount MapServer `export`/`tile` from `Spatial.Host`** (which owns
  `TileService` and the composer): splits one GeoServices resource across two
  route owners and leaks host-only types into the facade. Rejected.
- **Duplicate the style composer in the adapter**: two copies of the
  ADR-0047 assembly rule, free to drift. Rejected; the rule is shared.
- **Compute extents lazily/never**: a root with null extents confuses ArcGIS
  clients. Rejected in favour of a scan with a recorded follow-up.
- **Emit full Esri renderers (class breaks, unique value, labels)**: needs a
  style model the engine does not have and unbounded surface (ADR-0044
  explicit non-goal). Rejected; `drawingInfo` is the simple subset.
