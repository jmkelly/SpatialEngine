---
status: accepted
date: 2026-10-02
deciders: maintainer + agent
summary: The facade serves the OGC Simple Features DE-9IM table verbatim, the feature geometry as the matrix's left operand, with the pattern keyed on the pair's **dimension pair** for the two verbs OGC defines per dimension pair: `Within` is `T*F**F***`, the transpose of `Contains` and so one predicate with it; `Crosses` is `T**T*****` / `T*T******` / `0********`; and a dimension pair OGC defines no `Crosses` over — a point against anything — asks **no pattern at all** and reads false, which is what makes a point inside an area `Within` it rather than `Crosses` it (amends ADR-0036).
amends: ADR-0036
related: ADR-0156
---

# ADR-0106: The served `spatialRel` reading is the OGC DE-9IM pattern table

## Context

ADR-0036 made the Feature Service `spatialRel` verbs the DE-9IM intersection
patterns of `IGeometryRelations.Relate` and named the patterns. It left the
*reading* of the OGC table open: which pattern answers which verb, which
geometry is the matrix's left operand, and what a point against an area is.

That gap was filled twice by accident. A duplicate-agent collision produced two
complete, green implementations of SpatialEngine-u2x.2 — `SpatialRelationPredicates`
(abdb72b, merged) and `EsriSpatialPredicates` (c6602c0, unreachable) — and they
disagreed about two of the five verbs. The swarm stopped merging and stopped
spawning, because the disagreement was an architectural decision no worker had
been authorised to make. Six beads have landed on the served table since, each
correcting a row of it (u2x.35 the touches union, u2x.56 the dimension-keyed
overlaps and crosses rows, imj the grammar, dih the fixture table, ADR-0156 the
two readers of it), and each had to guess at the reading this record states.

## Decision

**Serve the OGC Simple Features DE-9IM table verbatim — `T`, `F` and the
dimension masks, the feature geometry as the left operand of the matrix — and
key the two dimension-dependent verbs on the pair's dimension pair.**

1. **`Within` is `T*F**F***`, spelled out in its own right.** There was no
   divergence to settle here at all. The discarded implementation evaluated
   `Contains` with the operands swapped, and a DE-9IM matrix read in the other
   frame is its transpose: transposing a mask swaps positions 2↔4, 3↔7 and
   6↔8, so the `F`s of `T*****FF*` at 7 and 8 land at 3 and 6, which is
   `T*F**F***`. One predicate, two spellings, equal over every pair measured
   (`Within_is_the_transpose_of_contains`, eleven pairs including a feature
   straddling the query's edge). The table names the verb and the Geometry
   Service serves it by name, so the mask is spelled where it is read.
2. **`Crosses` is the OGC triple, keyed on the dimension pair**: `T**T*****`
   (feature is the surface) / `T*T******` (query is the surface) /
   `0********` (line/line). The pattern asks for the *lower*-dimensional side
   to reach the higher-dimensional one's boundary, and the line/line row asks
   for a dimension-zero interior intersection, which is what separates two
   crossing lines (cross) from two collinear lines each reaching past the other
   (overlap).
3. **A dimension pair OGC defines no `Crosses` over names no pattern.** A point
   against anything, and a pair of surfaces, asks nothing and reads false. **A
   point inside an area is therefore `Within` it, never `Crosses` it** — and a
   point *on* an area's boundary is `Touches`, because the interiors are
   disjoint. This is the case the escalation named, and it is the reason the
   verb is keyed on the dimension pair rather than on a dimension mask.
4. **One table, no per-implementation copy.** The Feature Service query path and
   the Geometry Service `relation` operation read the same
   `SpatialRelationPredicates` entries, so the two endpoints cannot answer one
   pair of geometries differently (SpatialEngine-51k).

The record ratifies what the merged implementation already does. It rewrites no
served behaviour: every row it names has been on `main` since u2x.2, and the two
corrections that moved a row since (u2x.35's touches union, u2x.56's line/line
crosses and overlaps) are corrections *of* this reading, made against it.

## Alternatives

- **The dimension-masked reading (`1********` / `0********`, lines and areas
  only), which was read as "more JTS-native".** It asks a question the verb does
  not. `0********` says the interiors meet at dimension zero, which is true of a
  point *inside* an area, so it calls that pair `Crosses` — and then needs a
  hand-written dimension switch to take the answer back, which is a second rule
  to keep in step with the first. On the pairs the verb is actually for it is
  worse: a line crossing an area gives a one-dimensional intersection, so its
  `(1, 2)` case asks `0********` and answers false where `T*T******` answers
  true. Rejected: it agrees with the served reading on the point/area pair by
  accident and disagrees with it everywhere else.
- **Keep the two implementations and let a caller pick.** There is one
  `spatialRel` parameter with a closed grammar; a per-call reading of the OGC
  table is a second set of answers to one question with nothing to choose
  between them. Rejected.
- **Settle the reading by asking a real ArcGIS FeatureServer** (SpatialEngine-msc).
  That is still open and is the strongest evidence available, but the table
  cannot wait for it and the question is answerable now from the standard: the
  two readings differ on a pair the OGC defines, and the served one is the
  standard's.

## Not decided

- **Which geometry is the left operand of the matrix** — the dimension
  *direction*, feature against query or the reverse. The served path has always
  been feature-left, and this record pins the reading in that frame without
  claiming the frame is the right one (SpatialEngine-2ve).
  > **Update (2026-10-03):** decided by ADR-0171, against a live
  > FeatureServer rather than against the OGC table: `spatialRel` names the
  > feature's relation to the input geometry, so the served `Contains` is
  > `T*F**F***` and the served `Within` is `T*****FF*` — each the transpose of
  > what this record served. The frame itself (feature on the left) stands;
  > the two masks swap between the names, and the four symmetric verbs do not
  > move.
- **Whether the engine's `Relate` computes the matrix the standard means.** This
  record decides which pattern answers which verb; it does not decide that
  asking the pattern computes the matrix. That is ADR-0156's subject, and the
  oracle it keeps is what checks it.
> **Update (2026-10-01):** the engine's cell semantics are closed by ADR-0166:
> the served table is read off the matrix NetTopologySuite 2.6 reports, that
> matrix is the DE-9IM matrix, and the two ways into it are one computation.
> What a live ArcGIS Server makes of `Crosses` for a line lying wholly inside a
> polygon and touching its boundary is still open, and ADR-0166 names that pair
> as the one where the served reading and the provider's own predicate differ.

- **The edge semantics against a live ArcGIS Server** (SpatialEngine-msc) and
  **`esriSpatialRelContains` against a point query geometry**
  (SpatialEngine-aqy).

## Consequences

- The served behaviour is pinned at the two points where the readings
  disagreed, in
  `tests/unit/Spatial.Adapter.GeoServices.Tests/SpatialRelationReadingTests.cs`:
  the `Within` transpose equality, the straddling feature, the
  point-inside-an-area `Within`/`Crosses` pair, the pattern the match path
  actually asks in each operand order and each dimension pair, and the fact
  that a point pair asks *no* pattern. Success, failure and cancellation are
  covered; the cancellation case is the same cancellable `Relate` call every
  other verb makes. Restoring the discarded reading fails 16 of those 48 cases,
  so the pin is the divergence and not a restatement of the implementation.
- The point/area case is now answered by the dimension-pair gate rather than by
  the empty-boundary argument a reader of the table alone might reach for. Both
  give the same verdict; only one of them is a rule that can be got wrong
  independently of the pattern.
- ADR-0036 keeps naming the patterns and this record keeps naming which reading
  of them is served; a reader who wants the served verb for a pair wants this
  record and ADR-0036's later corrections together, not either alone.
- The dropped `EsriSpatialPredicates` (c6602c0) and its
  `SpatialRelExactnessTests.cs` stay unreachable. Branch deletion is the human's
  (SpatialEngine-6b4).
- **Cost:** the line/line reading is the one place the served table's answers
  depend on position 7 of `1*T***T**`, so a collinear pair whose span lies
  wholly inside the other line is neither `Overlaps` nor `Crosses` — the
  line/line containment, which no served verb names. That is the standard's
  answer, not a defect, and it is a sharp edge a client comparing against
  another implementation will meet.

## References

- ADR-0036 (the relation verbs and the pattern table this record reads)
- ADR-0156 (the two independent readers of that table; related, not amended)
- ADR-0053 (test-first: a defect gets a failing reproduction before the fix)
- OGC 06-103r4 §6.1.2.3, the DE-9IM pattern table
- `src/Spatial.Adapter.GeoServices/SpatialRelationPredicates.cs`
- `tests/unit/Spatial.Adapter.GeoServices.Tests/SpatialRelationReadingTests.cs`
- `architecture/references/geoservices-compatibility.md` §2
- SpatialEngine-onj (this decision), SpatialEngine-u2x.2 (the served
  implementation), SpatialEngine-u2x.7 (the discarded one), SpatialEngine-2ve
  (the direction), SpatialEngine-msc (a live server), SpatialEngine-6b4 (the
  branch deletion)

## Measurements

Over the fixtures this record's tests carry, the feature geometry on the left.
`m` is the intersection matrix as the engine computes it; the reference column
is NetTopologySuite's own `Crosses` over the same pair.

| pair (feature, query) | `m` | served | discarded | reference |
| --- | --- | --- | --- | --- |
| line inside area, area query | `1FF0FF212` | F | F | F |
| line crossing area, area query | `101FF0212` | **T** | F | **T** |
| line on area's edge, area query | `F11FF0212` | F | F | F |
| area across line, line query | `1F20F1102` | **T** | F | **T** |
| point inside area, area query | `0FFFFF212` | F | **T** | **F** |
| area around point, point query | `0F2FF1FF2` | F | **T** | **F** |

The point/area row is the one the two readings agreed on, and they agreed for
different reasons: the served reading gets it out of the dimension-pair gate, the
discarded one out of a hand-written switch. Every other row separates them.

For `Within`, the discarded implementation swapped the operands of the served
`Contains` rather than spelling its own mask. `SpatialRelationReadingTests`
asserts the two spellings equal over eleven pairs — including `straddle`, where
the feature runs from (5,0) to (15,10) across a query square of (0,0)-(10,10) —
and the served `Within` answers false for all three: a feature straddling the
edge, a feature containing the query, and a point on the query's boundary.

Measured with `dotnet test --filter FullyQualifiedName~SpatialRelationReadingTests`
against `main` as of 2026-10-02: 48 cases, 0 failed. The same run with
`SpatialRelationPredicates.CrossesPattern` temporarily replaced by the
discarded dimension-masked reading: 16 failed, 32 passed.