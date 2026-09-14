---
status: accepted
date: 2026-09-14
deciders: maintainer + agent
---

# ADR-0058: MapServer export parity — time, dynamicLayers, layerOption, cached-root honesty

## Context

The compatibility review (`research/compat/map-service.md` §§1–2, T-E)
records four export gaps against S2 (export-map/) and the G1 cached root
(`ground-truth/map-root-cached.WorldTopo.json`): `export` never reads
`time`/`layerTimeOptions`/`timeRelation` (while live roots advertise
`supportsTimeRelation`), never reads `dynamicLayers`, reads `layers` but
not `layerOption`, and the root omits `exportTilesAllowed` (T-041 owns
`exportTiles` itself). `MapExport.cs` parsed only
layers/bbox/size/bboxSR/imageSR/layerDefs/format/transparent/dpi.

The pieces already exist:

- The query path parses `time` with a closed grammar and matches it
  against date attributes in memory (`EsriFeatureQuery`/`FeatureQueryEngine`,
  T-022): any date value inside the extent matches; dateless features pass.
- `MapStyleProjection` maps the persisted MapLibre fragment onto Esri
  `simple`/`uniqueValue`/`classBreaks` renderers (ADR-0050); the reverse
  direction over the same subset turns a per-request `drawingInfo` back
  into a fragment the render pipeline already consumes.
- The render contract resolves each layer's store at the edge
  (`MapLayerSource`); the Web-Mercator scheme's LOD grid already follows
  the same origin/resolution family as the G1 cached root.

The constraints are the standing ones: Esri types stay inside
`Spatial.Adapter.GeoServices` (ADR-0005), no spatial algorithm in
`Spatial.Core`, `Spatial.PluginSdk` takes no packages, cancellable tasks
and structured failures (`invalid.arguments`, `not.found`,
`store.unavailable`).

## Decision

**Export reads all four parameters; the root advertises exactly what export
honours.**

1. **Time** (`time`, `timeRelation`, `layerTimeOptions`). `time` reuses the
   query grammar verbatim (instant or `start,end` with `null` infinity
   bounds, epoch milliseconds or ISO-8601). `timeRelation` accepts the
   documented relations (`esriTimeRelationOverlaps` default, `Contains`,
   `Within`) — the engine's date values are instants, so all three reduce
   to containment, and anything else is a typed `invalid.arguments`.
   `layerTimeOptions` carries per-layer `useTime` (default true) and
   `timeDataCumulative` (cumulative layers show everything up to the window
   end via an open start); a zero `timeOffset` is a no-op while a non-zero
   offset is a typed `invalid.arguments` — the engine has no per-layer
   time-shift model, so a shifted layer cannot be rendered honestly.
2. **The temporal extent rides the render contract.** `MapLayerSource`
   gains an optional core-typed `MapTimeExtent(StartMs, EndMs)`; the Skia
   pipeline drops features whose date values fall outside it after the
   store fetch, with the query path's rule (dateless features pass). A
   where-clause translation was rejected: the demo and memory stores
   reject or ignore store-level filters, so it would not hold everywhere;
   in-renderer filtering is correct on every provider, and a store
   pushdown is a future optimisation, not a requirement. The root
   advertises `supportsTimeRelation: true`.
3. **Dynamic layers** rebind by `source.mapLayer`/`mapLayerId` (same id
   overrides, new id appends; unknown ids are typed `not.found`) and may
   override the renderer. The override supports the projected subset —
   `simple`/`uniqueValue`/`classBreaks` over circle/line/solid-fill
   symbols — converted back to a MapLibre fragment. New data sources
   (joins, table queries, rasters), picture/text symbols, label overrides
   and non-zero transparency are typed `invalid.arguments`, never silent
   fallbacks. The root advertises `supportsDynamicLayers: true`.
4. **`layerOption`** (`all|visible|top`) is validated in the shared
   selection seam. The engine's layers are flat and always visible with no
   group hierarchy, so all three select everything and `layers` refines
   from there; anything else is a typed `invalid.arguments`.
5. **Cached-root honesty.** `singleFusedMapCache` with its `tileInfo` is
   emitted exactly when a tile scheme is served (the LOD replay proves the
   rows against the G1 cached root: resolutions tightly, scales within
   Esri's own display rounding — their table is not exactly
   resolution·96/0.0254, so byte-equality is not attainable and not
   asserted). `exportTilesAllowed: false` is always emitted: no packaging
   route exists, and T-041 owns the `exportTiles` scoping. `storageInfo`
   stays absent — there is no stored tile cache to describe.

## Consequences

- A time-aware web map's every-export `time` now filters instead of being
  ignored, with the same rule the query path applies; dateless layers
  render byte-identically with or without `time`.
- Clients can restyle a layer per request (the classification loop from
  ADR-0055 round-trips through `dynamicLayers`) without a second style
  model and without touching the renderer.
- The SDK gains one core-typed record and one optional field; Core, the
  providers and the neutral tile routes are untouched, and no package is
  added.
- Unsupported temporal/shape constructs fail loudly, so a client never
  mistakes an unfiltered render for a filtered one.

## Alternatives

- **Translate `time` into a `layerDefs`-style where fragment**: adapter-only
  change, but the demo store rejects attribute filters and the memory
  stores ignore them — temporal filtering would hold only on PostGIS.
  Rejected; the render contract is the store-independent seam.
- **Accept-and-ignore `dynamicLayers` drawing overrides or non-zero time
  offsets**: keeps naive clients rendering, but serves pixels the request
  did not ask for. Rejected; the repo's standing rule (T-024 audit) is
  loud errors over silent narrowing.
- **Hardcode Esri's LOD scale table into the scheme or adapter**: would
  make `tileInfo` byte-close to G1, but bakes one vendor's display
  rounding into neutral tiling math (or advertises scales the resolution
  does not derive). Rejected; tolerance-bounded replay is the honest
  assertion.
