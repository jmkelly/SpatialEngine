---
status: accepted
date: 2026-10-04
deciders: maintainer + agent
summary: The ADR-0175 model is implemented as `TemporalExtent` in `Spatial.Core` with a non-positional `DatasetDescription.TimeFields` designation, the query, identify and render readers all call it, a designated layer's pushed pre-filter is that rule's overlaps shape, and no layer carries a designation yet — so every served answer is unchanged and ADR-0100's typed reject stands.
amends: ADR-0175
related: ADR-0100, ADR-0110
consulted: ADR-0081
---

# ADR-0178: Land the feature temporal extent the way ADR-0175 decided it

## Context

ADR-0175 decided what a feature's temporal extent *is* — the interval between
the two date fields a layer's schema designates — and left the landing to a
follow-up. Nothing was implemented: no layer carried a designation, the rule
was still written three times (`FeatureSpatialMatcher.MatchesTime` for query
and the image-service catalog, `IdentifyFilters` for identify, and
`FeaturePipeline.MatchesTime` for render), and `FeatureMatchPushdown.Time`
pushed a clause shaped for the bag rule. `esriTimeRelationContains`/`Within`
stayed typed `invalid.arguments` (ADR-0100), which is correct: serving them is
what the flag flip in ADR-0175 §6 conditions on, and that is
SpatialEngine-bm7's bead, not this one.

Two of ADR-0175's requirements were about *how* the model lands rather than
what it is, and neither had been written down as code: the designation must be
invisible to record equality and to the append-only column-evolution check, and
the three readers must become calls to one rule rather than three rules that
agree by hand.

## Decision

**Land ADR-0175 as decided, additively, and let no layer carry a designation
yet.**

**1. The designation is a non-positional init-only member on
`DatasetDescription`.** `TimeFields` (a `TemporalExtentFields`: a start field,
an end field, either absent) is `public … { get; init; }` and defaults to
`null`. It is deliberately *not* a positional constructor parameter: a
positional member would move every construction of the type and enter its
record equality, so a layer that merely gained a designation would stop
comparing equal to the same layer described before — and those descriptions are
what `FeatureSchema.IsDecodableFrom` reads when it checks that a stored schema
is a prefix of the one being served.

**2. The model lives in `Spatial.Core.Features` as `TemporalExtent`**: two
inclusive bounds where a null bound is infinite, `From(feature, fields)`,
`FromMilliseconds(long?, long?)` for the window the request sides already
carry, the three relations (`Overlaps`, `Contains`, `Within`, over a
`TemporalRelation`), and `MatchesAnyDate(feature, window)` — the bag rule an
undesignated layer keeps. An empty extent (a designated start after its end)
matches nothing; the bounds are never swapped. A designated field that is null,
or that the schema does not have, reads as an infinite bound, which is what
makes a row with neither bound the whole line and therefore a match under every
relation.

Core stays structural: two bounds compared, read off an attribute bag. No Esri
names, no transport, no store, and `TemporalRelation` is the model's
vocabulary — a surface's own spelling of a relation is translated where it is
parsed, which is the adapter's job.

**3. The three copies become calls to it.** `FeatureSpatialMatcher.MatchesTime`
gains an optional `TemporalExtentFields?` and dispatches between the bag rule
(no designation) and `TemporalExtent.From(...).Overlaps(window)`; the
`MatchCandidate` carries the layer's `TimeFields` so every reader of it — the
Feature Service match loop, the image-service catalog query and the
relationship traversal — agrees. `IdentifyFilters` passes the dataset's
designation into the same call, and `FeaturePipeline.MatchesTime` (render, which
must not reference the adapter) passes the one it already holds from
`DescribeAsync`. `supportsTimeRelation` stays `false` and the two relations stay
rejected by name on an undesignated layer (ADR-0100, ADR-0081).

**4. A designated layer's pushed clause is the rule's overlaps shape over the
designated pair** — the designated start is not after the window's end, or the
designated end is not before its start, each disjuncted with "the field is
null". This is narrower than ADR-0175 §5 reads if §5 is taken to mean the
undesignated per-field clause: that clause asks every designated field to fall
*inside* the window, which drops a straddling row the extent rule matches, and
a pre-filter that drops an answer is not a superset. Overlaps is the superset
of all three relations, so one clause serves whichever relation the matcher
applies, and the pushed-result-⊆-scan conformance fixture is the proof. The
undesignated clause is byte-identical to what it was.

## Alternatives

- **Leave `FeatureMatchPushdown.Time` alone entirely** (ADR-0175 §5 read
  literally). Rejected: the pushed clause would be narrower than the matcher on
  exactly the rows the model was built for, so a designated layer's answers
  would depend on whether the store honoured the plan. What is pushed is a
  pre-filter, never the answer (ADR-0110); a pre-filter has to admit every row
  the rule can accept.
- **Put `TemporalExtent` in `Spatial.Contracts` next to the designation.**
  Rejected: nothing about it is contract vocabulary — it holds
  `DateTimeOffset` bounds and reads a feature, so it is Core's value family
  (`AttributeValue`, `Envelope`), and the renderer must reach it without
  touching the adapter.
- **Keep `TemporalRelation` out and give the extent three boolean methods
  only.** Rejected on its own merits; it is the switch the served relations are
  dispatched by, and a switch the surface parses into is one spelling away from
  being the surface's vocabulary.
- **Author the designation as part of this change** (publish configuration or
  ingest inference) so the feature is reachable. Rejected: that is ADR-0175's
  open question about *where* a designation comes from, it is a different
  bead's scope, and serving contains/within and flipping the flag is
  SpatialEngine-bm7's.
- **Validate a designation that names a field the schema lacks.** Rejected for
  now: it reads as an infinite bound, which is the same reading the row's own
  null takes, and where an operator's designation is authored is undecided.
  A layer whose designation is silently wrong is the cost named below.

## Not decided

- **Where the designation is authored** — publish configuration, ingest
  inference, or a per-request override. ADR-0175 left it open and this record
  does not answer it; the member it adds is the place any of those land.
- **Serving `esriTimeRelationContains`/`Within` and flipping
  `supportsTimeRelation`.** ADR-0175 §6 conditions both on a designated layer
  serving all three, and the flag stays `false` until then
  (SpatialEngine-bm7).
- **Whether the layer metadata emits `timeInfo`** once a designation makes it
  truthful.

## Consequences

- The duplication is gone: three rules become one value in `Spatial.Core` and
  three call sites, so the next change to the temporal model is one edit.
- No served answer moves. Nothing in the repository publishes a
  `TimeFields`, so every layer takes the bag branch and its answers are the
  ones it gave before — the pushdown clause for an undesignated layer is
  unchanged code on an unchanged shape.
- The cost is that the model is unreachable from the surface: an operator
  cannot yet designate anything, so `supportsTimeRelation` will read `false`
  after this change exactly as it read `false` before. The model is landed and
  inert.
- A layer that later gains a designation is a **behaviour change** for its
  `time` queries, and it is a *silent* one for its author unless the
  designation's origin is made visible (which is the undecided authoring
  question). That is the wart this record carries.
- An undesignated layer's pushed clause stays a superset; a designated layer's
  is now proven to be one by the pushdown conformance fixture rather than
  assumed.

## References

- ADR-0175 (what a feature's temporal extent is), ADR-0100
  (`supportsTimeRelation:false`, the typed rejects this record leaves standing)
- ADR-0110 (the store read is a pre-filter, never the answer), ADR-0081
  (advertise a capability only when behaviour proves it)
- `src/Spatial.Core/Features/TemporalExtent.cs`,
  `src/Spatial.Core/Features/TemporalExtentFields.cs`
- `src/Spatial.Contracts/Providers/DatasetDescription.cs` (`TimeFields`)
- `src/Spatial.Adapter.GeoServices/FeatureSpatialMatcher.cs` (`MatchesTime`,
  `MatchCandidate`), `IdentifyFilters.cs`, `RasterCatalogQuery.cs`,
  `FeatureRelationshipEngine.cs`, `FeatureMatchPushdown.cs` (`Time`)
- `src/Spatial.Rendering.Skia/Pipeline/FeaturePipeline.cs` (`MatchesTime`)
- Beads: SpatialEngine-jcu (ADR-0175), SpatialEngine-5ab (this record),
  SpatialEngine-bm7 (serve the other two relations)

## Measurements

The three copies the decision removes (`git grep -n MatchesTime`, 2026-10-02,
from ADR-0175) and where each got the designation from:

| Site | Role | Designation reaches it through |
| --- | --- | --- |
| `FeatureSpatialMatcher.MatchesTime` | query + image catalog | `MatchCandidate.TimeFields`, filled from `spec.Dataset.TimeFields` |
| `IdentifyFilters.MatchesFilters` | identify | `dataset.TimeFields` (it already held the description) |
| `FeaturePipeline.MatchesTime` | render | `description.TimeFields` from `DescribeAsync` |
| `RasterCatalogQuery.Match` | image catalog | `dataset.TimeFields` |
| `FeatureRelationshipEngine.RelatedForAsync` | relationship traversal | `traversal.Target.Related.Description.TimeFields` |
| `FeatureMatchPushdown.Time` | pushed pre-filter | `dataset.TimeFields`; overlaps shape over the designated pair |

The new rule is pinned by `TemporalExtentTests` (Core: the extent, its bounds,
the three relations and the bag rule), `FeatureSpatialMatcherTimeTests` (a
straddling designated row matches, an undesignated layer does not change,
reversed bounds match nothing), `MapIdentifyTimeTests` and `TemporalRenderTests`
(one implementation, three callers) and
`FeatureMatchPushdownTests.A_designated_layers_pushed_time_clause_is_a_superset_of_the_extent_rule`.
