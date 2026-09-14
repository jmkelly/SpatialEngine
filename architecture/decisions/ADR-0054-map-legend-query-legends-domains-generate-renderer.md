---
status: accepted
date: 2026-09-14
deciders: maintainer + agent
---

# ADR-0054: MapServer legend, queryDomains/queryLegends and generateRenderer are adapter projections

## Context

The compatibility review (`research/compat/map-service.md` §1, T-D) records
four missing MapServer resources against S4 and the G1
`map-legend.Census.json` fixture: `legend` (per-layer symbology),
`queryDomains` / `queryLegends` (service-level domain/legend queries) and
`generateRenderer` (server classification). The feature write-model track
(T-038) shares the `generateRenderer` gap but is not started; the map export
parity track (T-040) touches `MapExport.cs` but is not started either.

The pieces already exist:

- ADR-0050 projects each layer's persisted MapLibre fragment onto a
  `drawingInfo` (`simple`/`uniqueValue`/`classBreaks`), `labelingInfo` and
  `domains`. A legend is the same projection rendered as swatches.
- The adapter scans the store per root request for extents (ADR-0048 §4);
  classification is the same scan pattern over attribute values.
- The shared `EsriFilterClause` grammar already parses `where`; the layer
  metadata already validates fields against the catalogue schema.

The constraints are the standing ones: Esri types stay inside
`Spatial.Adapter.GeoServices` (ADR-0005), no spatial algorithm in
`Spatial.Core`, no packages for `Spatial.PluginSdk`, cancellable tasks and
structured failures.

## Decision

**All four resources are adapter projections; no SDK or Core change.**

1. **`legend`** (`/{service}/MapServer/legend`): one legend layer per
   published layer (`layerId`, `layerName`, `"Feature Layer"`, swatches,
   one `legendGroups` heading). Each renderer entry becomes one swatch:
   `uniqueValue` one entry per value, `classBreaks` one entry per break,
   `simple` a single entry; the group heading is the classified field (empty
   for `simple`). A layer with no projected renderer carries a single
   neutral swatch rather than an empty legend.
2. **Swatch bytes** are solid-colour PNGs of the entry's symbol colour,
   encoded by a framework-only writer (`PngSwatch`: 8-bit RGBA, filter-zero
   scanlines, `System.IO.Compression` for the IDAT). No renderer dependency
   crosses into the adapter; the `url` is a stable hash token, the
   `contentType` is `image/png`, and the size is 20×20 like the G1 fixture.
3. **`queryDomains`** returns each selected layer's projected `domains` map
   (the same object the layer metadata serves); **`queryLegends`** returns
   each selected layer's legend layer (the same object `legend` serves).
   Both accept the shared `layers` selection grammar (bare ids,
   `show:`/`hide:`/`all`); unknown ids are typed `not.found` rather than
   silently dropped.
4. **`generateRenderer`** (`/{service}/MapServer/{layerId}/generateRenderer`)
   classifies the layer's data for one request. It supports
   `classBreaksDef` with `esriClassifyEqualInterval` over a numeric field
   (`breakCount` 1–32, default 5) and `uniqueValueDef` with exactly one
   `uniqueValueFields` entry (at most 64 distinct values). It honours
   `where` through the shared filter grammar and cancellation through the
   scan. Anything else — quantile/natural-breaks methods, multi-field unique
   values, unknown fields, a non-numeric break field, an empty match set —
   is a typed `invalid.arguments`, never a silent fallback. Symbols follow
   the layer's geometry family (marker/line/fill) with deterministic
   qualitative/sequential palettes.
5. **Single home for classification.** `MapGenerateRenderer` in
   `Spatial.Adapter.GeoServices` is the map-service `generateRenderer`
   implementation; the feature write-model track (T-038) reuses it rather
   than duplicating it. `MapExport.cs` is untouched (T-040 overlap: none).

## Consequences

- An ArcGIS client reading `legend` sees swatches that always agree with
  the layer `drawingInfo` (same projection), and `queryDomains` agrees with
  the layer `domains` — one source, three shapes.
- `generateRenderer` gives clients a real classification loop (equal
  intervals, value enumeration) without a second style model and without
  touching the renderer: the returned renderer is data-derived metadata,
  not a persisted style change.
- The adapter gains two cohesive files (`MapLegend`, `MapGenerateRenderer`)
  plus wire records; the SDK, Core, the renderer and the providers are
  untouched, and no package is added.
- Unsupported classifications are explicit errors, so a client never
  mistakes a fallback for a classification.

## Alternatives

- **Render swatches through `IMapRenderer`/Skia**: true symbology
  thumbnails, but leaks renderer types into the adapter against ADR-0005 and
  couples a metadata read to the raster pipeline. Rejected; solid-colour
  swatches carry the classification colour, which is what the legend
  entries classify by.
- **Add a classification verb to `Spatial.PluginSdk`**: would let providers
  push classification down, but no provider implements it and the adapter
  scan already matches the ADR-0048 extent precedent. Rejected; a provider
  pushdown is a future optimisation, not a requirement.
- **Support every Esri classification method now** (quantile, natural
  breaks, standard deviation): needs sorting-based statistics the task does
  not require and widens the failure surface. Rejected; equal-interval plus
  unique-value enumeration covers the client classification loop, and the
  rest fail loudly.
