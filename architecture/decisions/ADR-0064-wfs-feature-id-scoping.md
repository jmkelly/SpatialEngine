---
status: accepted
date: 2026-09-14
deciders: maintainer + agent
---

# ADR-0064: WFS GetFeature ids are scoped per typeName

## Context

The T-068 OpenLayers/Leaflet client-compat proof (on main) found that one
multi-typename WFS collection carries duplicate ids: the memory ingest
numbers features per dataset (ADR-0042, `MemoryIngestPlan.Materialise` starts
`nextId` at 1 per dataset), so `cap.lines` holds 1,2 and `cap.polys` holds
1,2. A `GetFeature` over `cities,routes,zones` serves all 12 features
(`numberMatched: 12`, pinned by T-068), but the GeoJSON carries duplicate
`id` values and OpenLayers documents that a same-id feature is not added to
the source — so the genuine client holds 10 of 12. T-068 pins that honest
behaviour; T-084 changes it.

Two scopings were on the table:

1. **Globalise memory-store ids at ingest** — one counter across datasets,
   so raw store ids are already unique.
2. **Qualify WFS feature ids per typeName** — `<typeName>.<id>`.

## Decision

**Qualify WFS GetFeature ids per typeName as `<typeName>.<id>`.**

Store identities are per-dataset by design: `FeatureId` uniqueness is a
per-dataset contract (the same factoring as PostGIS, where each table has
its own sequence), and ADR-0042/ADR-0043 own those identity rules.
Globalising the memory counter would invent a cross-dataset identity the
store contract never promised, leak WFS presentation into ingest, and still
leave every other store (PostGIS sequences, demo data) colliding the same
way. Qualification fixes all stores at once, at the layer where the
collision exists: the multi-typename response document.

The shape mirrors the existing WMS GetFeatureInfo GML, which already emits
`gml:id` as `{dataset}.{id}` — per-layer scoping is the established
precedent on this boundary. The qualifier is the OGC-visible layer name
(`MapLayer.Name ?? Dataset`, i.e. what capabilities advertise and requests
select by), applied on every GetFeature response — single- and
multi-typename alike — so one feature has one stable WFS identity however
it is paged. Per-resource addressing (`featureId`/`resourceId`) stays an
honest `InvalidParameterValue` reject: only `bbox` subsetting is
implemented, and that honesty is unchanged.

## Consequences

- A multi-typename GetFeature collection carries no duplicate ids, so a
  genuine client addresses every feature: the T-068 OL proof's 10-of-12
  becomes 12-of-12 once its pinned honest count follows up.
- Single-typename ids change shape too (`cairo` becomes `Cities.cairo`):
  clients that stored raw store ids as WFS ids must re-read them. The
  store ids themselves are untouched — only the WFS presentation qualifies.
- The WMS identify shape (raw ids in text/html/xml/json, qualified only in
  GML `gml:id`) is unchanged: this decision scopes the WFS adapter only.

## Alternatives

- **Globalise memory-store ids at ingest:** would fix the memory provider
  only, break the per-dataset identity model (ADR-0042/ADR-0043), and leave
  PostGIS/demo collisions in place. Rejected.
- **Qualify only multi-typename responses:** would keep single-layer ids
  stable, but one feature would then carry two WFS identities depending on
  how it was requested — unstable for caching and paging clients.
  Rejected; one feature, one WFS id.
