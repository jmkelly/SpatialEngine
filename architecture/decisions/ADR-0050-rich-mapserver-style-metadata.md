---
status: accepted
date: 2026-09-16
deciders: maintainer + agent
---

# ADR-0050: Rich MapServer style metadata, labels and domains are an adapter projection

## Context

`architecture/map-service-plan.md` shipped M0–M4 and recorded **richer style
metadata (class breaks, unique value, labels) as a standing non-goal**
(ADR-0048: "Richer Esri renderer types … are **not** produced"). The
compatibility review (`architecture/references/geoservices-compatibility.md`
§4) still lists the MapServer image resource (§4.7) and the
symbol/renderer/label/domain objects (§12–15) as absent.

The pieces have since changed:

- ADR-0047 persists each publication layer's style as a **MapLibre fragment**
  (an array of style-layer objects) and `PublicationMapStyle` composes the
  fragments into one document (ADR-0044 dialect: `background`/`fill`/`line`/
  `circle`, flat paint, `filter` expressions, zoom windows).
- ADR-0048 projects the flat colour of the first `fill`/`line`/`circle`
  fragment onto an Esri `simple` renderer. Everything richer is dropped.
- The engine catalogue (`IDataCatalogue.DescribeAsync`) exposes a
  `DatasetDescription` with a `FeatureSchema` of `FieldDefinition`s (name,
  kind, nullability, description). It has **no first-class domain vocabulary**.

So the question is how far the adapter can project the *existing* persisted
style and catalogue metadata onto §12–15 without inventing a second style
model, adding a spatial algorithm, or letting Esri types cross into
`Spatial.PluginSdk`.

Two constraints pin the answer:

- **ADR-0005 / principle 8:** every Esri renderer, symbol, label and domain
  type stays inside `Spatial.Adapter.GeoServices`. `Spatial.PluginSdk`
  references only `Spatial.Core` and takes no packages.
- **The MapLibre fragment is the single style source** (ADR-0047). A rich
  projection must be *derived* from it, not stored beside it.

## Decision

**The adapter projects richer `drawingInfo`, `labelingInfo` and `domains`
from the persisted MapLibre fragment and the catalogue schema. Nothing new
is persisted, no SDK contract changes, and every Esri type stays in the
adapter.**

### 1. Renderer projection (spec §15)

The adapter groups a layer's fragments by symbol kind and inspects the
`filter` of the same-kind siblings (the ADR-0044 `filter` subset). The most
specific kind present, in the order `fill` → `line` → `circle`, wins.

| Fragment shape | Projected renderer |
| --- | --- |
| one same-kind fragment, or no recognisable pattern | `simple` (today's flat-colour mapping) |
| several same-kind fragments, each `["==", field, value]` or `["in", field, v…]`, all one field | `uniqueValue`: `field1`, `uniqueValueInfos` (one entry per value; `in` expands), `defaultSymbol` from an unfiltered sibling if one exists |
| several same-kind fragments, each `["all", [">=", field, lo], ["<", field, hi]]`, all one numeric field | `classBreaks`: `minValue` (lowest `lo`), `classBreakInfos` ordered by `hi` |
| anything else | `simple` from the first same-kind fragment |

The symbol of each class/unique entry is the existing flat `esriSFS`/
`esriSLS`/`esriSMS` mapping of that fragment's paint. A `filter` is never
turned into SQL or pushed to a provider; it is read as JSON at the adapter
edge. Colour parsing is the shared `EsriColor` subset.

**Range filters are metadata-only until the renderer grows them.** The Skia
`FilterReader` (ADR-0044) currently reads `==`, `!=`, `has`, `!has`, `in`,
`all`, `any`, `none` and `!`; it rejects `>=`/`<` with a typed
`invalid.arguments`. Therefore a `uniqueValue` layer renders server-side
through `export`/tiles exactly as its `drawingInfo` describes, while a
`classBreaks` layer serves an accurate `drawingInfo` but its server render is
rejected by the renderer's documented "unsupported is rejected, never
flattened" rule. Extending the filter subset (and therefore server-side
class-breaks rendering) belongs to the raster track's labels/symbols work
(ADR-0049/R6); this ADR does not touch `Spatial.Rendering.Skia`.

### 2. Label projection (spec §12.7/§14)

A fragment of type `symbol` becomes one Esri label class when it carries a
**single-field** text expression:

- `layout.text-field` is `["get", "field"]` or the string `"{field}"`;
- `paint.text-color` (flat CSS colour, default black), `layout.text-size`
  (number, default 12) and `layout.text-font` (first family, default
  `Arial`) shape the `esriTS` symbol;
- `layout.text-anchor` maps to the point placement; lines use
  `esriServerLinePlacementCenterAlong` and polygons
  `esriServerPolygonPlacementAlwaysHorizontal`;
- `labelExpression` is the Esri `[field]` form; `minScale`/`maxScale` stay
  `0` (zoom-to-scale mapping is not attempted).

Any other `symbol` fragment (multi-field `concat`, `icon-image`, etc.) is
**not projected**; the layer simply carries no label class for it. This is
the small, explicit subset the task asks for in the absence of ADR-0049.

### 3. Domains (spec §13)

Domains are **derived from the projected renderer's field**, validated
against the catalogue (`dataset.Schema.IndexOf(field) >= 0`), and emitted
both as a layer-level `domains` map and on the matching `fields[].domain`:

- `uniqueValue` on a field → a `codedValue` domain whose `codedValues` are
  the renderer's own values;
- `classBreaks` on a field → a `range` domain `[minValue, lastClassMaxValue]`.

The catalogue exposes the field's existence and kind (the "field metadata"),
not a domain object; there is deliberately **no new domain vocabulary in
`Spatial.Core`/`Spatial.PluginSdk`**. A first-class catalogue domain
capability is the recorded follow-up, exactly as ADR-0048 recorded a store
extent capability.

### 4. MapServer image (§4.7)

The image resource is only defined for **picture marker / picture fill
symbols** (`esriPMS`/`esriPFS`): its `imageId` is the `url` of such a
symbol. The engine's style dialect has no picture symbols and the renderer
produces no stored symbol images, so §4.7 does not map onto the existing
render/tile SDK contracts. The route
`/{service}/MapServer/{layerId}/images/{imageId}` is mounted and returns a
typed `not.found` error explaining that picture-symbol images are not
produced — never a stub body and never a generic routing 404.

### 5. Capabilities and non-goals

The root capability string stays `Map,Query,Data`; the added resources are
all under the existing `Map`/`Data` routes, so no capability is added or
removed. The standing non-goals (relationships/attachments/HTML popup,
time, versioning, editing) are unchanged.

This ADR **supersedes the "rich style metadata is a non-goal" line** in
`architecture/map-service-plan.md` (both the header note and the §4 M4
entry).

## Consequences

- An ArcGIS client reading a rich layer's `drawingInfo` sees a real
  `uniqueValue`/`classBreaks` renderer, its `labelingInfo`, and `domains`
  for the rendered field — metadata that until now silently degraded to a
  flat symbol.
- The adapter grows one cohesive projection file (`MapStyleProjection`) and
  a handful of internal Esri records; the SDK, Core, the renderer and the
  providers are untouched. The architecture guardrails
  (`PluginSdk_references_only_core`, package allowlists) keep passing with no
  change.
- The MapServer `image` route is honest: it exists, it is typed, and it does
  not pretend the engine supports pictures.
- `classBreaks` layers are metadata-accurate but their server render is
  rejected until the renderer's filter subset grows; that dependency is
  explicit rather than hidden.
- A first-class catalogue domain (or a richer symbol model) is the recorded
  follow-up; neither is required for the §12–15 surface to be useful.

## Alternatives

- **Add a domain/symbol model to `Spatial.Core`/`Spatial.PluginSdk`**:
  would let the adapter project first-class metadata, but touches core
  values, the codec and every provider, and duplicates a model the persisted
  MapLibre style already carries. Rejected as out of proportion.
- **Store Esri renderers beside the MapLibre fragment**: two style models
  that can drift, against the ADR-0047 single-source rule. Rejected.
- **Serve `drawingInfo` from data-driven MapLibre expressions (`match`,
  `step`, `interpolate`)**: idiomatic MapLibre, but the ADR-0044 dialect is
  explicitly flat-paint + `filter`, so this would project from a document
  the engine's own dialect does not define. Rejected; the `filter`-based
  projection stays inside the documented dialect.
- **Return a stub body for §4.7**: hides a real capability gap from clients
  and violates the "reject, don't flatten" rule. Rejected.
- **Extend the Skia filter subset here**: violates the MapServer track's
  boundary (the raster renderer is owned elsewhere) and this ADR's scope.
  Deferred to ADR-0049/R6.
