---
status: accepted
date: 2026-09-16
deciders: maintainer + agent
---

# ADR-0047: Publication layers carry optional render style

## Context

A publication (ADR-0041) is a named, ordered projection of datasets onto a
protocol surface. Layers carry `Dataset`, `LayerId` and `Name` only, so a
service cannot express *how its layers should draw*.

Three existing pieces made that gap concrete:

- `map-composer-plan.md` records per-layer MapLibre style as a **client-side
  authoring aid**; the published `Publication` "carries ordered layers and
  stable ids only", because MapServer `drawingInfo` (M4 of
  `map-service-plan.md`) had no home.
- The renderer (ADR-0044) already consumes a small MapLibre style subset
  (`background`/`fill`/`line`/`circle`, colour, opacity, width, radius,
  visibility), and `LayerStyle` in `apps/workbench-web/src/composer.ts` is
  exactly that subset in a typed form.
- A request to seed realistic, *styled* map and feature services on demand
  showed that a style manifest cannot live in the seed script: the style is a
  property of the service, not of the tool that created it.

The standing rule is that contract changes land with an ADR
(`architecture/distilled/README.md` §"How to change the architecture").

## Decision

**1. `PublicationLayer` gains an optional core-typed `LayerStyle`.**

`Spatial.PluginSdk.LayerStyle` is a five-value record — `Color`
(`#rrggbb`), `Opacity` (0–1), `LineWidth`, `Radius` and `Visible` — mapping
exactly onto the renderer's supported subset and the composer's existing
model. `PublicationLayer(Dataset, LayerId, Name, Style = null)` stays
source-compatible; a layer without a style renders with adapter defaults.
Nulls are meaningful: the style stays optional because a data-only
publication (a FeatureServer) has no draw semantics.

**2. The style is data, not a package.**

It lives in `Spatial.PluginSdk` alongside `Publication`; it introduces no
dependency and no protocol type. There is deliberately **no** MapLibre or Esri
`drawingInfo` shape in the contract — adapters lower the style, exactly as
`Spatial.Adapter.GeoServices` lowers a publication to Esri JSON and the render
pipeline lowers MapLibre JSON to a draw plan. The registry (ADR-0041) persists
it verbatim in `publications.json`; `PublicationValidator` validates the
colour grammar and numeric ranges so a bad style is `invalid.arguments`, not a
render-time surprise.

**3. Adapters honour it where they already render.**

- The GeoServices adapter projects a styled `PublicationKind.Map` layer to a
  `drawingInfo.simple` renderer with an `esriSMS`/`esriSLS`/`esriSFS` symbol
  (M0/M4 subset of `map-service-plan.md`).
- The host may read the style when a future route renders a publication; the
  renderer's own `POST /api/render` contract is unchanged (the caller still
  sends MapLibre JSON), so no existing route or client breaks.
- FeatureServer ignores the style: symbols are a drawing concern.

**4. MapServer M0 becomes real.**

`PublicationKind.Map` previously existed but was stored only; the catalog and
routes served `FeatureServer` alone. Serving the **data-only** MapServer (M0 of
`map-service-plan.md`: root, layer, `layers`, `query`) is part of accepting
this ADR, so a sealed publication is discoverable as `MapServer` and its
`drawingInfo` is meaningful. `export`/`tile` (M2/M3) stay out of scope, and
the service advertises `capabilities: "Query,Data"` accordingly — it does not
claim `Map`.

## Consequences

- A seed tool can create self-describing styled services through the public
  admin API with no client-side state; the style round-trips through
  `publications.json`.
- The style vocabulary is intentionally the renderer's subset. Labels, class
  breaks and scale dependencies remain non-goals and would be a later ADR (M4
  proper).
- `Publication` grows by one optional field; existing wire payloads, declared
  publications and clients are unaffected.
- MapServer M0 computes extents by scanning its layers (the stores expose no
  extent capability yet). That is acceptable for the discovery/query use M0
  targets and is recorded as a limitation; a future store-owned extent
  capability replaces the scan without changing the wire shape.

## References

- ADR-0041 (publications/ingest), ADR-0044 (raster pipeline),
  ADR-0005/0033 (core-typed, package-free SDK)
- `architecture/publishing-and-ingest-plan.md`,
  `architecture/map-service-plan.md`,
  `architecture/map-composer-plan.md`
