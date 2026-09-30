---
status: accepted
date: 2026-09-30
deciders: maintainer + agent
summary: The MapServer layer record advertises `hasZ`/`hasM` under ADR-0084's rule verbatim — the ordinates the store proves, and nothing else.
amends: ADR-0084
---

# ADR-0125: The MapServer layer record carries the same declared ordinates

## Context

ADR-0084 made the dataset description carry the coordinate layout its store
declares, and §4 of that record put `hasZ`/`hasM` on **the layer metadata** —
but read narrowly, that was the Feature Server layer resource, because
`EsriLayer` is the Feature Server's record. The Map Service has its own layer
record (`EsriMapLayer`, spec §4.2) written explicitly in `MapServiceModel.cs`,
and it had no ordinates at all.

That omission is visible in the protocol. A MapServer layer resource is the
same document as a FeatureServer layer resource with a `drawingInfo` attached,
and the captured upstream services say so: `tests/fixtures/arcgis/captured/`
holds a MapServer layer for a 3D layer (`geonames-mapserver/layer-1.json`)
carrying `"hasZ": true`, and 2D MapServer layers
(`canvas-world-dark-gray-base-mapserver/layer-0.json`,
`arcx-…-activityprojectareas-01-mapserver/layer-0.json`) carrying neither key.
So the keys are part of the published interop surface, and the engine's map
surface had to be able to say them.

It is also an inconsistency inside the engine rather than a missing feature.
The map surface already projects a per-layer description from **the same
`DatasetDescription`** (`MapLayerInfo` → `MapServerResources.Layer`), so the
one dataset was described as 3D by the Feature Server and 2D by the Map Server.
A client that preflights a layer — the branch ADR-0084 §Context describes, and
`hasZ` is the branch key — would read the map surface and conclude the
elevations were not there.

## Decision

**The MapServer layer record advertises `hasZ`/`hasM` under ADR-0084's rule
verbatim: the ordinates the store proves the geometry column declares, and
nothing else.**

`EsriMapLayer` gains `HasZ` and `HasM` as `bool?`, positioned after the
existing optional `Domains` parameter, and `MapServerResources.Layer` fills
them from `dataset.GeometryLayout` — `true` for the ordinate the layout
carries, `null` otherwise, which the serializer drops. A two-dimensional
dataset, and a geometry column declared plain `geometry` (which constrains
nothing and so proves nothing), advertise neither key, exactly as the Feature
Server layer resource does.

There is no second rule, and no map-specific view of the layout: a MapServer
layer is never a table, so `EsriLayerModel.Describe`'s `isTable` guard has no
counterpart here, and the projection is the same one the Feature Server applies
to the same description.

## Consequences

- A 3D dataset served through a Map Service describes itself as 3D. The two
  surfaces cannot disagree about the same dataset, because they read the same
  description through the same rule.
- The wire change is additive and conditional: a two-dimensional layer's
  response is byte-identical to what it was, and a 3D layer gains exactly the
  keys upstream serves, at the same point in the document. No published key
  changes type or meaning, so no client that works today breaks.
- The honesty cost ADR-0084 names is paid identically on both surfaces: a
  dataset whose column declares no typmod advertises nothing even if its values
  carry a Z, and the map surface does not get a cheaper way to find out.
- Nothing crosses a new wall. `CoordinateLayout` is a `Spatial.Core` type
  (ADR-0084) and both wire records stay inside `Spatial.Adapter.GeoServices`
  (ADR-0005, ADR-0035). `Spatial.Contracts` is untouched.

## References

- ADR-0005 (implementation types stay in their implementation)
- ADR-0035 (GeoServices boundary adapter)
- ADR-0081 (advertised capability flags — the honesty rule)
- ADR-0084 (§4, the layer metadata, extended to the MapServer layer record)
- ADR-0091 (the ArcGIS REST store proves its ordinates from the layer's own
  declaration — the store side of the same claim)
- `architecture/references/geoservices-compatibility.md` §4, §7
- `tests/fixtures/arcgis/captured/geonames-mapserver/layer-1.json` (a 3D
  MapServer layer) and `…/canvas-world-dark-gray-base-mapserver/layer-0.json`
  (a 2D one)
