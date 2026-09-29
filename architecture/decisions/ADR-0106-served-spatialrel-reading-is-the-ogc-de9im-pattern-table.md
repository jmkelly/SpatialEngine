---
status: accepted
date: 2026-09-29
deciders: maintainer + agent
amends: ADR-0036
---

# ADR-0106: The served `spatialRel` reading is the OGC DE-9IM pattern table

## Context

ADR-0036 made the Feature Service `spatialRel` verbs the DE-9IM
intersection patterns of `IGeometryRelations.Relate` and left the *reading*
of the OGC table open. A duplicate-agent collision then produced two
complete, green implementations of that bead, and they disagreed about two
of the five verbs (SpatialEngine-onj):

| | kept: `SpatialRelationPredicates` (abdb72b, merged) | dropped: `EsriSpatialPredicates` (c6602c0, unreachable) |
| --- | --- | --- |
| `Within` | its own pattern `T*F**F***` | `Contains`'s `T*****FF*` with the operands swapped |
| `Crosses` | the OGC pair `T**T*****` / `T*T******`, selected by which side is lower-dimensional | the dimension-masked JTS patterns `1********` / `0********`, lines and areas only |
| `Touches` | two masks: `F***T****`, and `F**T*****` when a point is involved | the three JTS masks `FT*******`, `F**T*****`, `F***T****` |

The escalation that stopped the swarm read the `Within` row as a real
divergence: a "distinct" `WithinPattern` that "additionally requires the
feature's interior to miss the query's exterior", so the two "can disagree
on a feature straddling the query's edge". They cannot. A DE-9IM matrix
read in the other frame is its transpose, and transposing a mask swaps
positions 2↔4, 3↔7 and 6↔8 — so the `F`s of `T*****FF*` at 7 and 8 land
at 3 and 6, which is `T*F**F***`. One predicate, two spellings. Measured
over eleven polygon/line/point pairs the two spellings agree on every one,
including a feature straddling the query's edge
(`SpatialRelationReadingTests.Within_is_the_transpose_of_contains`).

The `Crosses` row is a real divergence, and the drop is the other way
round. The dimension-masked reading was read as "more JTS-native", but it
asks a question the verb does not: `0********` says the interiors meet at
dimension 0, which is true of a point *inside* an area, so that reading
calls a point in a polygon `Crosses` — and had to carry a hand-written
dimension switch to take that one case back. On the pairs the verb is
actually for it is worse. A line crossing an area gives a one-dimensional
intersection, so the `(1, 2)` case asks `0********` and answers false; the
served `T*T******` answers true, which is what a line crossing an area is.
Measured, against the reference implementation's own `Crosses`:

| pair (feature, query) | matrix | `T**T*****` | `T*T******` | `1********` | `0********` | reference |
| --- | --- | --- | --- | --- | --- | --- |
| line inside area, area query | `1FF0FF212` | T | **F** | T | F | **F** |
| line crossing area, area query | `101FF0212` | F | **T** | T | **F** | **T** |
| line on area's edge, area query | `F11FF0212` | F | F | F | F | F |
| area across line, line query | `1F20F1102` | **T** | T | T | F | **T** |
| point inside area, area query | `0FFFFF212` | F | **F** | F | **T** | **F** |
| point inside area query, area feature | `0F2FF1FF2` | **F** | T | F | T | **F** |

The point/area row is the last one the two implementations agreed on, and
they agreed for different reasons: the kept reading gets it out of the
table (a point's own boundary is empty, so matrix positions 2 and 4 cannot
meet), the dropped one by refusing the dimension. The served reading also
agrees with the reference on every row above, and it asks the pattern for
the lower-dimensional side rather than the higher one — a distinction that
is load-bearing in both directions (`T**T*****` is true of an area whose
line query lies wholly inside it).

The dimension **direction** — feature against query, or query against
feature — is not settled here: that is SpatialEngine-2ve, and the tests
here pin the reading in the feature's frame without claiming the frame is
the right one.

## Decision

**The facade serves the OGC Simple Features DE-9IM table verbatim: `T`
(meets), `F` (disjoint) and dimension masks, the feature geometry as the
left operand of the matrix, and a dimension-selected pattern only for the
two verbs OGC itself defines per dimension pair.**

1. **`Within` is `T*F**F***`, written out in its own right.** It is the
   transpose of the served `Contains` (`T*****FF*`) and the table is closed
   under transposition, so the two are one predicate; the table names the
   verb and the Geometry Service serves it by name, so the mask is spelled
   where it is read. Pinned: a feature that straddles the query's edge is
   not `Within` it; a feature sharing part of the query's *boundary* is,
   because the mask excludes the feature's boundary against the query's
   exterior, not its interior.
2. **`Crosses` is the OGC pair, selected by which side is the
   lower-dimensional geometry**: `T*T******` (position 2 — the lower-
   dimensional interior reaches the higher-dimensional boundary) when the
   query is the higher dimension, `T**T*****` (position 4) when the feature
   is. Equal-dimensional pairs are not `Crosses` at all. The point/area
   case is **not** served as `Crosses`: a point inside an area is `Within`
   it, and the table says so without a special case. A point *on* an
   area's boundary is `Touches`, not `Crosses` — the interiors are
   disjoint.
3. **One table, no per-implementation table.** The Feature Service query
   path and the Geometry Service `relation` operation read the same
   `SpatialRelationPredicates` entries, so the two endpoints cannot answer
   one pair of geometries differently (SpatialEngine-51k).

The dropped implementation's `Touches` row was the better of the two — it
carried the third mask, `FT*******`, where the kept table has two — but
`Touches` was not one of the two readings under dispute and the gap is a
defect, not a decision. It is filed as SpatialEngine-4n2 against the kept
implementation, with the measurement: a point or line feature lying on a
query polygon's boundary is served `Touches: false` (matrix `F0FFFF212` or
`F11FF0212` matches neither `F***T****` nor `F**T*****`, while the
reference says true). The reverse operand order is served correctly, which
is why the envelope-driven feature tests never saw it. Filed as
SpatialEngine-u2x.35.

## Consequences

- `src/Spatial.Adapter.GeoServices/SpatialRelationPredicates.cs` is the one
  table, unchanged by this record: the decision ratifies what the merged
  implementation does rather than rewriting it. The dropped
  `EsriSpatialPredicates` (c6602c0) and its `SpatialRelExactnessTests.cs`
  stay unreachable — their branch tips are `origin/main`, and the refs to
  them are for the human who owns branch deletion (SpatialEngine-6b4).
- The reading is pinned at the points where the two implementations
  disagreed:
  `tests/unit/Spatial.Adapter.GeoServices.Tests/SpatialRelationReadingTests.cs`
  — the `Within` transpose equality, the straddling feature, the
  point-inside-an-area `Crosses`/`Within` pair, and the pattern the match
  path actually asks for in each operand order. Success, failure and
  cancellation are covered; the cancellation case is the same cancellable
  `Relate` call every other verb makes.
- `esriSpatialRelContains` against a point query geometry, the edge
  semantics against a live ArcGIS Server, and the engine's own cell
  semantics are **not** settled here and stay with SpatialEngine-msc,
  SpatialEngine-2ve, SpatialEngine-aqy and SpatialEngine-imj. Deciding the
  pattern table does not decide whether the engine's `Relate` computes the
  matrix the standard means.

## References

- ADR-0036 (the relation verb the `spatialRel` patterns are asked of)
- ADR-0035 (GeoServices boundary adapter)
- OGC 06-103r4 §6.1.2.3, the DE-9IM pattern table
- `architecture/references/geoservices-compatibility.md` §2
