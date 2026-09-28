---
status: accepted
date: 2026-09-28
deciders: maintainer + agent
---

# ADR-0083: The distance band, the per-feature centroid and the Z/M output selection are served

## Context

`architecture/references/geoservices-compatibility.md` §7.1 listed Feature
Service query parameters as deliberate non-goals. Most were honest: `text`
needs an inverted index, `historicMoment`/`gdbVersion` need versioning in the
store, `queryRelatedRecords` needs a relationship model. Three were "the
engine has no verb" wearing a rejection as a disguise, and a rejection is a
permanent answer while a verb is a work item:

- `distance`/`units` — a distance-range filter is a spatial verb the engine
  already has the pieces for: `IGeometryOperations.Buffer` over the query
  geometry, and the curated Esri unit table the Geometry Service's `unit`
  already resolves.
- `returnCentroid` — a per-feature centroid, which is
  `IGeometryMeasures` without a centre-of-mass verb.
- `returnZ`/`returnM` — recorded as "the engine geometry model carries Z/M
  but the Esri codec serves 2D". Checked, per the bead's instruction:
  `Spatial.Core`'s canonical binary codec round-trips `Xyzm` exactly (pinned
  by `GeometryCodecTests.RoundTripCases`), and the Esri codec *reads* and
  *writes* Z and M ordinates. What the projection actually got wrong was
  narrower and more embarrassing: the writer emitted a three-ordinate Esri
  coordinate array **without** the `hasZ`/`hasM` flag that says whether the
  third number is a Z or an M, which the codec's own reader uses to decide.
  So a 3D line was served in a form the same codec would read back with the
  wrong ordinate. A true `returnZ`/`returnM` was then rejected outright,
  which is the worst of both: the ordinate is present and ambiguously
  labelled, and the client is told it cannot have it.

## Decision

Serve all three, and record the Z/M finding.

- **`distance`/`units`** (Feature, Map and Image catalog query): parsed into
  an `EsriQueryDistance` and applied as a buffer of the query geometry in the
  **layer CRS** — the same transform-then-buffer the Geometry Service uses for
  `bufferSR`/`outSR`/`inSR` chaining. The unit code is resolved by the shared
  `EsriUnitCode` table with the Geometry Service's rule (a linear code needs a
  projected CRS, an angular code a geographic one, because the engine's verbs
  are planar and there is no geodesic verb), so the two surfaces cannot drift.
  Every `spatialRel` then runs over the buffered band, so `distance` composes
  with the exact DE-9IM verbs (ADR-0036/SpatialEngine-u2x.2) rather than
  bypassing them. `distance` without a `geometry` and `units` without a
  `distance` are named failures, never silent drops (the T-024 policy).
- **`returnCentroid`**: a new `IGeometryMeasures.Centroid` verb — the area
  centroid of a polygon, the length midpoint of a line, the point itself for
  a point, the merged centroid of a collection, an empty point for an empty
  geometry — implemented over NTS's `Centroid`. The adapter writes it beside
  the feature's geometry through a `Func<IGeometry, IGeometry?>` on
  `EsriFeatureWriteOptions`, so no implementation type crosses the codec.
  Deliberately *not* the envelope middle: that is structural (core already
  has envelopes) and wrong for every concave or asymmetric shape, which is
  exactly the kind of "cheap" answer the T-024 audit exists to stop.
  A request with no feature to attach it to (`returnGeometry=false`, or a
  result shape that carries no features) is rejected by name.
- **`returnZ`/`returnM`**: an `EsriOrdinateOutput` selection on the Esri
  writer. The writer now emits `hasZ`/`hasM` for whatever ordinates it
  writes — a 3-ordinate array is Z with only `hasZ`, M with only `hasM`, the
  codec's own read rule — and a false flag narrows the output to the
  requested ordinates (no Z, no M, or M without Z). Both default to true,
  which is the existing behaviour plus the missing flags; a client that wants
  2D asks for it, and a client that asks for Z on a 2D layer gets 2D because
  the store returned 2D, not because the projection dropped it.

`Spatial.Core` is untouched: a centroid is an algorithm (implementation), the
ordinate selection is a projection concern (adapter/codec), and a distance
band is a composition of two verbs the engine already had. The one contract
change is the additive `IGeometryMeasures.Centroid`; no client SDK changes,
because the .NET and TypeScript SDKs are the host HTTP API clients and do not
wrap the geometry verbs.

## Consequences

- `§7.1` loses three rows and the ones that stay are restated as reasons
  rather than missing verbs. The `returnZ`/`returnM` finding is recorded
  there: the canonical binary round-trips Z/M and the loss was Esri-JSON
  codec depth (missing flags plus a blanket rejection), now fixed.
- A `distance` band is planar in the layer CRS, as every other engine verb
  is; there is still no geodesic buffer, and a linear `units` code on a
  geographic layer says so.
- The unit-code table and the projected/geographic rule are shared by the
  Geometry Service and the query, so a code accepted by one is accepted by
  the other.
- Store Z/M fidelity is the store's own business: the projection serves
  whatever the store returned. Layer metadata does not yet advertise
  `hasZ`/`hasM` (tracked as its own bead), so a client cannot preflight it.
