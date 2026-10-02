---
status: accepted
date: 2026-10-02
deciders: maintainer + agent
summary: The feature-query plan carries **no** spatial-relation term: its spatial component stays the query geometry's `BoundingBox` pre-filter, and every `esriSpatialRel` topology verb is answered by the adapter over the rows that box admits. A pushdown face for the DE-9IM relations is admissible only when it provably cannot change an answer, and it cannot today — the served table and a store's own spatial predicates are measured to disagree, the served reading is still open on three beads, and the relation's own reductions (`returnCountOnly`, `outStatistics`, paging) are not expressible in the plan without it, so pushing would freeze a reading in every store's SQL to buy a row count that is not the answer (amends ADR-0074 §8).
amends: ADR-0074
related: ADR-0036, ADR-0106, ADR-0110, ADR-0156, ADR-0166
---

# ADR-0167: Keep the topology verbs adapter-side; the plan's spatial component is the envelope

## Context

ADR-0074 §2 landed the feature-query plan and its pushdown rule: a provider
pushes what its dialect can express and evaluates the residual over the rows it
fetched, so `QueryAsync(plan)` is the evaluation of `plan` over the whole
dataset, every time. §8 kept the plan's spatial component a `BoundingBox`
pre-filter and left the DE-9IM `spatialRel` verbs to `IGeometryRelations.Relate`
in the adapter, naming this as the open question: whether an optional
spatial-relation term belongs on the plan, each store either evaluating it or
omitting the capability, with the adapter as the fallback.

The question was worth asking because the asymmetry it sat on was the epic's
Tier 1 finding. Rendering, MVT, WMS and WFS push a box and a filter down; the
GeoServices feature, match, identify and find paths used to read whole datasets
and filter feature by feature. That is largely fixed — the match envelope
compiles onto the plan (ADR-0110) and the query path pushes its reductions
(ADR-0098) — but a topology request still gets no further than the box.

What it gets is already most of what a pushdown could honestly give it. Every
served relation implies an intersection, so one box is a superset for all six
verbs and rides the same plan, and the adapter then decides each candidate
(ADR-0110, ADR-0106). The residue is the store-side predicate itself, and one
class of reduction: a query whose answer depends on the relation cannot have
its `returnCountOnly`, its `outStatistics` or its paging pushed, because the
store can count the rows in the box and that is not the count of the matches.

So the question is narrow: is the relation worth a contract face, and what would
make it safe?

## Decision

**Do not put a spatial-relation term on the plan.** The plan's only spatial
member stays the query geometry's `BoundingBox`, every served `spatialRel`
stays an adapter-side verb over the rows that box admits, and no store declares
a capability for it.

The rule that decides every future case is this one:

1. **A pushdown face is admissible when it cannot change an answer.** The box
   qualifies: it is a superset for every served verb, and the adapter still
   decides each row, so which store is behind the layer is invisible in the
   answer. A relation term does not qualify today, for two measured reasons.

2. **A store's spatial predicate is not the served reading.** The served table
   is the OGC pattern table read off the matrix this engine computes (ADR-0106,
   ADR-0166), and a provider's named predicate — `ST_Crosses`,
   `geometry.STCrosses` — is its own reading of the same row. Measured: over
   the shared fixture table the two agree on every single-part check but the
   one ADR-0169 serves deliberately, and they part company on multi-part
   operands, where ADR-0166 measured 16 divergences of 1,536, all `Crosses`,
   in two named classes. So a store
   implementing the term with its own predicate does not answer the same
   question, and the served answer becomes a property of which store is behind
   the layer. A store that instead compiled the *served mask* would need its
   matrix pinned cell-for-cell against this engine's; nothing pins that for
   GEOS or T-SQL, and ADR-0166 pins it only for the in-process provider.

3. **The served reading is still open.** Three beads are deciding it
   (SpatialEngine-7qk the alternation read strictly, SpatialEngine-msc the
   semantics a live ArcGIS Server serves, SpatialEngine-2ve the operand
   direction). A term pushed today is today's reading compiled into every
   store's SQL, and moving it afterwards is a migration across stores rather
   than a change to one adapter table.

**What the term would have to satisfy when it is admissible**, so this decision
is revisitable rather than a refusal: the served reading is closed by those
beads; the term is core-typed (a relation name over the plan's geometry and a
named geometry, or an equivalent) and carries the *served mask*, not a
provider's predicate; every store that answers feature queries has been
measured to agree with `IGeometryRelations.Relate` on the boundary battery
(the 0-D operand, the vertex-only contact, the line wholly inside an area
touching its boundary, the straddling feature, the point inside an area) with
the divergence list closed at zero; and a store that cannot is omitted from the
face, with the adapter the fallback, so the term stays an optimisation and never
an answer.

**The cost, stated.** A topology request's reductions stay in the adapter: a
`returnCountOnly` over `esriSpatialRelWithin` counts the matches rather than
the rows in the box, and the two are different numbers on the same layer. That
is the price of the decision and it is not an oversight — a store-side count of
the box is a different question.

## Alternatives

- **Put the term on the plan with a per-store capability flag and the adapter
  as fallback.** This is the shape the bead named. It fails on rule 2: an
  optional face means the answer differs by deployment, and the flag turns a
  store's reading of a row into a client-visible difference. Rejected.
- **Compile the served *mask* down rather than a named predicate** (`ST_Relate`
  matched against the pattern, which Postgres can express and T-SQL cannot). It
  is the honest version of the term and the right long-term shape, but it still
  needs the served reading closed (rule 3) and the store's matrix pinned against
  this engine's (rule 2). Admissible later under the stated conditions; refused
  now because neither is true.
- **Let a store narrow to its own predicate as a *pre-filter*, exactly as the
  box is one.** Rejected: a pre-filter must be a superset, and the named
  predicate is not — on the 16 ADR-0166 pairs it excludes features the served
  table accepts. A pre-filter that drops a served answer is a wrong answer.
- **Push the box harder instead — a candidate-set term that is still only a
  box.** Already done (ADR-0110); there is nothing left in the plan's spatial
  member that is honest and narrower than the query envelope.
- **Push the relation and keep verifying adapter-side over whatever the store
  returns.** Rejected: verification cannot repair a dropped row. The store
  returns what it believes matched, and a row it wrongly excluded is gone before
  the matcher sees it. This is the difference between a pre-filter and an
  answer, and it is the reason the box works and the term would not.

## Not decided

- Whether the term is ever added. The conditions above say what would settle
  it; the beads named there are the evidence.
- Whether the adapter's own per-candidate cost is worth attacking on another
  axis (a store-side envelope-plus-attribute index, a cached candidate set) —
  that is an optimisation question, not this one, and no bead names it.
- The live-server edge semantics (SpatialEngine-msc) and the alternation's
  strict reading (SpatialEngine-7qk), which this record does not prejudge.

## Consequences

- `tests/unit/Spatial.Adapter.GeoServices.Tests/SpatialRelationPushdownTests.cs`
  is the conformance, over all 289 ordered fixture pairs crossed with the six
  served verbs: the plan a topology request compiles to is the query
  geometry's envelope and no other member (1,734 cases), and every pair a verb
  accepts is admitted by that box — the superset property the pre-filter rests
  on, so a change that made the box narrower than a superset fails here rather
  than dropping served features. The measured divergence is pinned beside it:
  the served table against the provider's own named predicates is 1 of 1,734 on
  single-part pairs — the pair ADR-0169 serves deliberately, added as the
  `line-touch-edge` fixture when this battery was found to carry no line that
  reached an area's boundary from inside (SpatialEngine-sck) — and the
  multi-part pair where they part company is spelled out. Success, failure and
  cancellation are covered — the cancellation case is the same cancellable
  match every other verb makes.
- Both pins were confirmed to bite. Narrowing the pushed box by one unit in
  `FeatureMatchPushdown.Box` fails the superset cases; restoring the discarded
  dimension-masked `Crosses` reading fails both divergence tests.
- The residual is a test rather than a note: a `returnCountOnly` topology query
  is asserted to serve the adapter's count where the box admits a larger set,
  so a later "optimisation" that takes the count from the store fails.
- `FeatureQuery` is unchanged, so no store implementer and no client is
  affected. The decision removes a possible contract face rather than adding
  one, which is the cheap direction.
- The cost a reader should expect: on a large layer, a topology query reads the
  rows in the query envelope into the host and relates them there. That is
  bounded by the envelope, not by the layer, and it is the honest bound while
  the served reading is open.

## References

- ADR-0074 (the plan, its pushdown rule, and §8's open question), ADR-0036
  (the relation verbs), ADR-0106 (the served pattern table), ADR-0110 (the
  match envelope's box pre-filter and the superset rule), ADR-0156 (two readers
  of the table), ADR-0166 (the cell semantics and the divergence measurement),
  ADR-0098 (the query path's reductions), ADR-0053 (test-first), ADR-0150 (the
  shape of a record)
- `src/Spatial.Contracts/IDataStores.cs` (`FeatureQuery`),
  `src/Spatial.Adapter.GeoServices/FeatureMatchPushdown.cs`,
  `src/Spatial.Adapter.GeoServices/FeatureSpatialMatcher.cs`,
  `src/Spatial.Adapter.GeoServices/SpatialRelationPredicates.cs`,
  `src/Spatial.Adapter.GeoServices/StoreQueryPath.cs`,
  `architecture/distilled/contracts.md`
- SpatialEngine-8ab (this decision), SpatialEngine-aqy (ADR-0166's
  measurement), SpatialEngine-7qk, SpatialEngine-msc, SpatialEngine-2ve (the
  three beads the served reading still waits on)

## Measurements

`dotnet test --filter FullyQualifiedName~SpatialRelationPushdownTests`, .NET 10,
2026-10-02: **1,540 cases, 0 failed** (1,536 pair × verb cases, the
divergence measurement, the multi-part pair, the count residual and the
cancellation).

| measurement | result |
| --- | --- |
| plan shape, 289 ordered fixture pairs × 6 served verbs | the plan is the query geometry's envelope; `Ids`, `Where`, `Projection`, `Order`, `Limit`, `Offset`, `Cursor` all null |
| superset property over the same 1,734 | every pair a served verb accepts is admitted by the pushed box |
| served table vs the provider's named predicates, 289 pairs × 6 verbs | **1 divergence of 1,734** on single-part fixtures: the `line-touch-edge` row, `Crosses`, served `true` against the predicate's `false` (ADR-0169) |
| the same over multi-part operands (ADR-0166's battery, 256 pairs × 6 verbs) | **16 divergences**, all `Crosses`: 6 area/line pairs where the line lies wholly inside the area and touches its boundary, 10 pairs with a 0-D operand |
| count residual: `geometry=2,2,4,4`, `esriSpatialRelWithin`, `returnCountOnly` | box admits 2 rows, served count is 1 |

2026-10-02, re-measured by SpatialEngine-sck: the battery grew a fixture. The
single-part table carried no line that lies wholly inside an area and reaches
its boundary from inside, so this record's "0 divergences of 1,536" was true
of the fixtures it walked and silent on the class ADR-0169 serves
deliberately. With `line-touch-edge` (5,2)-(10,5)-(5,8) in the table the
figure is 17 fixtures, 289 ordered pairs, 1,734 checks and exactly one
divergence — the pair ADR-0169 pins — and both the served table and the
Geometry Service's `relation` cross-check now state that exception rather than
ruling the class out. The decision is unchanged and the superset property is
unaffected; only the coverage and the count are.

Mutation checks, to show the pins are the claim rather than a restatement of
the implementation: insetting the pushed box by one unit in
`FeatureMatchPushdown.Box` fails the superset cases across the battery; serving
the discarded dimension-masked `Crosses` (`0********` for every dimension pair)
fails the divergence measurement and the multi-part pair.
