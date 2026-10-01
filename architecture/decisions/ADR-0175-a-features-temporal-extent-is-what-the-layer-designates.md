---
status: accepted
date: 2026-10-02
deciders: maintainer + agent
summary: A feature's temporal extent is the interval its layer's schema designates as the start and end date fields — not a min/max over whatever date attributes a row happens to carry — and one rule in `Spatial.Core` evaluates all three relations against it for the query, identify and render readers alike; a layer with no designation keeps ADR-0100's typed reject.
related: ADR-0058, ADR-0100
consulted: ADR-0081, ADR-0110
---

# ADR-0175: A feature's temporal extent is what the layer designates

## Context

ADR-0058 claimed that Esri's three time relations "all reduce to
containment" because "the engine's date values are instants". ADR-0100 found
that claim false in the code and rejected the two relations that depend on it:
`MapExportTime.ParseTimeRelation` accepted `esriTimeRelationContains` and
`Within` and both call sites discarded the parsed value, so they were served as
overlaps while the root advertised `supportsTimeRelation: true`. ADR-0100
closed that in the flag direction — the flag says `false`, the two relations
are typed `invalid.arguments` on export and identify — and refused to serve
them, because serving them requires **a feature temporal extent**, and the
engine models a feature's dates as a bag of `DateTimeOffset` attribute values
matched by "any value inside the window". It filed the model as the decision
to take rather than the plumbing to change.

The bag is the whole problem. The engine has no way to say which of a row's
dates bound it and which are incidental, so there is nothing to compare a
requested window against. The rule is written three times —
`FeatureSpatialMatcher.MatchesTime` (query and image-service catalog),
`FeaturePipeline.MatchesTime` (render) and `FeatureMatchPushdown.Time` (the
pushed pre-filter) — and the duplication is deliberate, because the renderer
must not reference the adapter and the adapter must not reference the renderer.
So the model has to be expressible once and callable from all three, or the
readers drift the first time one of them is touched.

## Decision

**A feature's temporal extent is the interval between the two date fields its
layer's schema designates, and one rule in `Spatial.Core` evaluates the
overlaps, contains and within relations against it.**

**1. The extent is designated per layer, not derived per row.**
`DatasetDescription` carries an optional designation naming the row's start
date field and its end date field; either may be absent, and naming the same
field for both is a point. A layer with no designation has **no** feature
temporal extent: it keeps the rule it has today — "any date value inside the
window, and a feature with no date values matches unconditionally" — and
`esriTimeRelationContains`/`Within` stay typed rejects on it, exactly as
ADR-0100 left them.

The designation is additive and invisible to the record's equality: a
non-positional init-only member, not a constructor parameter, so no existing
`DatasetDescription` construction and no append-only column-evolution check
moves. All three readers already hold a `DatasetDescription` for the layer
being read, so nothing new has to be plumbed to reach it.

**2. The extent's bounds are inclusive and a null bound is infinite**, the
rule the request side already applies to `time`. A designated start with no
designated end is `[t, +∞)`; a row whose designated fields are both null is
`(-∞, +∞)`, the undated feature; start equal to end is the point `[t, t]`. A
row whose designated start is later than its end is an empty extent and matches
no relation at all: the bounds are not swapped, because inventing an extent the
data does not have is the same error as guessing one.

The undated row is deliberate: the whole line as its extent means it matches
every relation, so the engine's existing "a feature with no date values matches
unconditionally" rule stays true under all three with no special case.

**3. The three relations, over inclusive bounds** (matching the request-side
bounds, so touching counts), with a null request bound infinite:

- **overlaps** — the extents intersect: `start ≤ window.End && end ≥ window.Start`.
- **contains** — the feature extent covers the window: `start ≤ window.Start && end ≥ window.End`.
- **within** — the feature extent is covered by the window: `start ≥ window.Start && end ≤ window.End`.

**4. The rule lives once, in `Spatial.Core`, and is called by all three
readers.** `Spatial.Core.Features` owns the extent value, its construction from
a `Feature` and the designation, and the three interval relations. That is
Core staying structural — two bounds compared, read off an attribute bag, no
Esri names, no transport, no store — in the same family as `AttributeValue` and
`Envelope`. The Esri spelling of a relation stays in the adapter where it is
parsed today. The three call sites become the query matcher and the
image-service catalog (already one method), the identify path (already
delegating to it), and the render pipeline; the duplication ends.

**5. The pushed pre-filter stays in the overlaps shape for all three
relations.** Contains and within both imply overlap — an extent that covers the
window, or is covered by it, intersects it — so one pushed clause is a superset
under every relation and only the matcher distinguishes them. ADR-0110's rule
holds unchanged: what is pushed is a pre-filter, never the answer, and its
pushed-result-⊆-scan conformance fixture stays the proof.

**6. `supportsTimeRelation` flips only when a designated layer serves all
three.** It is `false` now and stays `false` through the implementation,
because a flag is advertised exactly when the behaviour proves it (ADR-0081).
Only when a layer with a designation can answer all three relations does the
root advertise `true` — and a layer without one keeps refusing the other two by
name, because it has no extent to compare a window against, which is the honest
reject ADR-0100 chose and this record does not overturn.

## Alternatives

- **Min/max over the row's date attributes.** Rejected: it changes the meaning
  of `time` on a surface already served. A row with `start_date` and
  `end_date` gets an extent spanning the gap between two instants the client
  may not consider one thing, and a row with an unrelated `recorded_at` and
  `expires_at` gets an extent nobody declared. The engine cannot tell those
  cases apart — every `DateTimeOffset` attribute is interchangeable today, which
  is why there is no extent. A designation is opt-in and per layer, so a wrong
  extent is wrong in one describable place.
- **An extent the store declares.** Rejected: a store can declare the
  *layer's* range (`timeInfo.timeExtent`) and per-column statistics, not a
  feature's. The relations are evaluated per feature against the row in hand;
  a layer-wide declaration cannot answer "does this row contain the window".
- **Derive the extent on the fly and keep the two readers duplicated.**
  Rejected: this is the divergence the bead exists to prevent. The reason the
  rule is written twice today is a project boundary, and Core is the one place
  both sides may reach.
- **Serve contains/within on undesignated layers by reading them as the single
  instant inside the window** — the reduction ADR-0058 asserted. Rejected: it is
  exactly the silently-wrong answer ADR-0100 removed, and with a multi-date row
  it is not even equivalent to any interval relation.
- **Treat a null designated bound as *no extent* rather than infinite.**
  Rejected: it would make contains and within vacuously true for every undated
  row — the relation would be a no-op on exactly the layers clients ask it
  about — and it needs a second null rule beside the request-side one.

## Not decided

- **An undesignated layer with exactly one date field as time-aware** with an
  instant extent. That is the one case where the bag reading and the extent
  reading coincide, so it is the cheapest place to start serving the relations
  — but it is a guess about which layers clients mean, and it wants a client
  trace or a real time-enabled dataset.
- **Where the designation is authored** — publish configuration, ingest
  inference, or a per-request override. This record fixes that it is per layer
  and schema-level; how an operator supplies one is a separate decision.
- **Whether the layer metadata should then emit `timeInfo`.** A designation
  makes it truthful, but emitting it is a compatibility-surface change with its
  own flag honesty to answer.
- **Whether `layerTimeOptions.timeDataCumulative` should affect the extent.**
  It currently does nothing, and this record does not give it meaning.

## Consequences

- The two rejected relations become implementable without touching a served
  answer: no layer has a designation, so every request resolves the same rule
  it resolves today, and ADR-0100's typed reject is what a client still gets.
- One rule replaces three, so a future change to the temporal model is one edit
  rather than three that must be kept in agreement by hand.
- The cost is a schema-level concept the engine did not have: an operator must
  declare which dates bound a feature, and a layer that has not is served by the
  bag rule forever, so `supportsTimeRelation` will read `false` on most layers
  even after the model lands. That is the main reason a client may never send
  the other two relations, and it is what to revisit if a trace says otherwise.
- An undesignated layer that gains a designation is a **behaviour change** for
  its `time` queries: overlaps stops meaning "any instant inside" and starts
  meaning "the extent intersects". That is the intended consequence, and it is
  why the designation is per layer and describable rather than inferred.
- An undated row matches every relation, so contains and within cannot exclude
  a row whose designated fields are both null — the record's largest known
  wart.

## References

- ADR-0058 (map export `time`, `dynamicLayers`, cached-root honesty)
- ADR-0100 (`supportsTimeRelation:false`, typed rejects for contains/within)
- ADR-0081 (advertise the capability flags the served surface earns)
- ADR-0110 (the store read is a pre-filter, never the answer)
- `src/Spatial.Adapter.GeoServices/FeatureSpatialMatcher.cs` (`MatchesTime`)
- `src/Spatial.Adapter.GeoServices/IdentifyFilters.cs` (`MatchesFilters`)
- `src/Spatial.Adapter.GeoServices/FeatureMatchPushdown.cs` (`Time`)
- `src/Spatial.Rendering.Skia/Pipeline/FeaturePipeline.cs` (`MatchesTime`)
- `src/Spatial.Contracts/Providers/DatasetDescription.cs` (the designation's home)
- `research/compat/map-service.md` §2 (the gap this record decides)
- Beads: SpatialEngine-oas (the flag direction), SpatialEngine-jcu (this record)

## Measurements

The three copies of the temporal rule, and who already holds the layer
description each would need (`git grep -n MatchesTime`, 2026-10-02):

| Site | Role | Already holds a `DatasetDescription` |
| --- | --- | --- |
| `FeatureSpatialMatcher.MatchesTime` | query + image-service catalog | yes (`spec.Dataset`) |
| `IdentifyFilters.MatchesFilters` | identify | yes (`dataset`); delegates to the above |
| `FeaturePipeline.MatchesTime` | render | yes — `DescribeAsync` at `FeaturePipeline.cs:27` |
| `FeatureMatchPushdown.Time` | pushed pre-filter | yes (`dataset.Schema.Fields`) |

The current rule is pinned by `FeatureSpatialMatcherTimeTests`,
`MapExportTimeTests`, `MapIdentifyTimeTests`, `GeoServicesMapTests`,
`GeoServicesMapExportTests`, and the pushdown superset property by
`FeatureMatchPushdownTests`.