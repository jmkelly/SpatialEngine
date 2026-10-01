---
status: accepted
date: 2026-10-01
deciders: maintainer + agent
summary: **The engine's `Relate` computes the DE-9IM matrix the standard means, and the two ways into it — `Relate(a, b)` and `Relate(a, b, pattern)` — are one computation, so a cell cannot be reported one way by the rendering and another by a pattern.** NetTopologySuite 2.6 is pinned as the reader and is not wrapped. Two readings of its cells are recorded rather than corrected, because both are the reading the served table already rests on: a 0-D operand contributes **no boundary** (a point on a line's endpoint meets it in the line's boundary row, position 4, and is not disjoint), and a vertex-only contact is **dimension 0** in position 5, one cell away from the dimension 1 of an edge-sharing contact. Against the provider's own named predicates the served table diverges on exactly two measured classes, both named and both served as ADR-0106 states; the first — `Crosses` for a line lying wholly inside an area and touching its boundary — is new here.
amends: ADR-0036
related: ADR-0106, ADR-0156, ADR-0165
---

# ADR-0166: The engine's `Relate` is the DE-9IM matrix, read as JTS reads it

## Context

ADR-0036 made `IGeometryRelations.Relate` the DE-9IM relation verb and left the
matrix's *cells* to the provider; ADR-0106 settled which pattern answers which
served verb and said so twice over — it decided the reading of the pattern
table, and it listed "whether the engine's `Relate` computes the matrix the
standard means" as not decided, along with "the engine's own cell semantics"
(SpatialEngine-aqy). ADR-0156 then decided how the table is read by two
independent readers, and ADR-0165 that its `De9im` column is the exact matrix
rather than a display of one. Between them they check that the adapter serves
what the table says. None of them checks that the table's matrices are the ones
the engine reports, which is the question this record answers.

The bead that produced it reported three divergences between
`Relate(a, b, pattern)` and `Relate(a, b)` in NetTopologySuite 2.6, and framed
the gap as one a future pattern could fall into: a pattern that leans on a cell
the engine reports loosely would be wrong in a way the predicate-level
cross-check would not catch.

All three reports do not survive measurement, and one of them is the opposite
of a defect:

1. *"A 0-D first operand is treated as having an EMPTY boundary (classic
   DE-9IM/JTS treats the point as its own boundary), so a line against a point
   at its endpoint reports bnd(intersection) = F and the pair reads as
   disjoint."* The line (0,0)-(10,0) against a point on its endpoint reads
   `FF10F0FF2`: the contact is in position 4, the line's **boundary** against
   the point's **interior**, so the pair is not disjoint and the served
   intersect union answers true. An empty point boundary is JTS's reading and
   PostGIS's (`ST_Boundary` of a point is empty); the report read the
   position-4 cell as position 1.
2. *"Vertex-only contacts report int(bnd) = T where the classic matrix has F,
   so two squares touching at a corner read like two sharing an edge."* They
   read `FF2F01212` against the edge-sharing `FF2F11212` — position 5 is `0`
   against `1`. One cell apart, and named exactly.
3. *"The same pair can report different cells through the two paths:
   `Relate(g)` prints a transposed matrix whose pos0 disagrees with the
   pattern path's."* `Relate(g)` reaches the matrix through
   `GeometryRelate.RelateV1`, and the pattern overload is the same call
   matching the result: the two are equal cell for cell over every ordered pair
   of the characterisation battery, and over 41,439 valid pairs drawn at
   random from a six-unit grid (point, line, area and every multi-part
   spelling, validity checked).

What is left after the three is the cross-check the report said could not be
made, and it does find something.

## Decision

**Keep NetTopologySuite 2.6 as the reader of the matrix, unwrapped; record the
two readings its cells carry; and pin both the agreement and its exceptions.**

- **The verb is not wrapped.** `Relate` answers a pattern exactly as the matrix
  reads, cell by cell, and the matrix is the one the standard means. No cell is
  recomputed in `Spatial.Operations.NetTopologySuite` and no pattern is
  answered by anything other than the matrix.
- **The two paths are one computation**, so a pattern and the rendered matrix
  cannot disagree, and a divergence between them is a change in the provider
  rather than in the reading.
- **A 0-D geometry contributes no boundary.** Its own boundary row and column
  are empty wherever it is an operand, so a meeting with a point is reported
  through the other geometry's interior or boundary and never through the
  point's own boundary. This is the JTS and PostGIS reading, kept because the
  served table's `Touches` union is built on it: the contact lands in position
  2 for a point and position 4 for a line, which is why the union carries all
  three OGC masks (SpatialEngine-u2x.35).
- **A vertex-only contact is dimension 0** in position 5, and an edge-sharing
  contact dimension 1 — recorded because it is the cell a report is most likely
  to read as interchangeable.
- **The served table's answers are pinned against the provider's named
  predicates, with every divergence named.** A pair where the two disagree and
  the divergence is not in the measured list is a failing test.

## Alternatives

- **Wrap `Relate` in `Spatial.Operations.NetTopologySuite` and compute the nine
  cells independently**, so a provider change could not move a served answer.
  Rejected: it buys a second implementation of a solved problem, it would put
  a topology engine's edge cases into this repository rather than the provider's
  test corpus, and it is what ADR-0106's decision deliberately does not do —
  the served answers are the OGC table's, read off whatever matrix the engine
  reports. The pinned characterisation is the cheaper guard: it fails the day
  the provider moves a cell.
- **Upgrade NetTopologySuite and re-run the spatialRel matrix suite.** Named by
  the bead as the other way out. Still available and still the right move if a
  provider defect turns up; a version is pinned in
  `Directory.Packages.props` and a change to it is a change to what every
  served relation answers, so it is not something to do while looking for
  something else.
- **Correct the served `Crosses` masks to match the provider's predicate**, so
  the two agree everywhere. Rejected: ADR-0106 serves the OGC alternation
  verbatim, and the provider's predicate is the one adding a condition the
  alternation does not state. Making them agree by moving the table would
  change served answers on the strength of a provider implementation rather
  than the standard.
- **Treat the divergence as a defect and wrap only `Crosses`.** Rejected for the
  same reason as the third option, and it would be a special case in the verb
  with a special case in its tests.

## Not decided

- **Which reading of the mixed-dimension `Crosses` row a client comparing
  against ArcGIS will want** (SpatialEngine-msc, the live-server edge
  semantics). This record settles what this engine reports, not what a client
  should expect; the divergence is the sharp edge either way.
- **The direction of the matrix's operands** (SpatialEngine-2ve). Unchanged
  here: the frame is still the feature's.
- **Non-simple and self-touching inputs**, where the provider's named
  predicates disagree with the served table in the opposite direction (served
  false, predicate true). Every pair measured that way carried a repeated
  vertex in the line or the ring; the served surface reads such a geometry as
  what its coordinates say, and no fixture here is degenerate enough to pin.

## Consequences

- `tests/unit/Spatial.Operations.NetTopologySuite.Tests/NtsRelateCellSemanticsTests.cs`
  is the characterisation: the two paths equal cell for cell over 256 ordered
  pairs, a 0-D operand's boundary row and column empty over the same 256, the
  point/line and vertex/edge pairs spelled out with their matrices, the served
  table against the provider's predicates over 256 × 6, and the documented
  divergence list asserted **closed** — a divergence that starts or stops is a
  failing test, not a note.
- The documented list is 16 of 1,536 checks, all `Crosses`, in two classes: the
  line lying wholly inside the closed area with its interior reaching the area's
  boundary (position 4 non-empty, position 7 empty) — six pairs, served `true`
  against the predicate's `false` — and a 0-D operand on one side, where the
  served table names no mask at all and reads false while the predicate is
  component-wise over a `MultiPoint` and reads true (ten pairs, ADR-0106).
- A client comparing this engine with NetTopologySuite's own predicates will
  find `Crosses` disagreeing on the first class and nothing else; the
  compatibility reference says so in the `relation` row, where a client reads
  it.
- The cost is stated rather than hidden: for a line wholly inside an area and
  touching its boundary, this engine says `Crosses` and NetTopologySuite does
  not. That is the OGC alternation read verbatim (ADR-0106), it is now named in
  two places a client can find, and it is the one answer in the served table a
  reader of this record should expect a provider to match differently.

## References

- ADR-0036 (the relation verbs and the DE-9IM verb this refines), ADR-0106 (the
  served pattern table, the alternation and the 0-D-operand decision),
  ADR-0156 (the two readers of the table), ADR-0165 (the `De9im` column is the
  matrix), ADR-0053 (test-first), ADR-0143 (a check that gates rather than
  formats)
- `src/Spatial.Operations.NetTopologySuite/NtsGeometryRelations.cs`,
  `src/Spatial.Adapter.GeoServices/SpatialRelationPredicates.cs`,
  `architecture/references/geoservices-compatibility.md` (the `relation` row)
- SpatialEngine-aqy (this decision), SpatialEngine-1dg (the rendered matrix and
  the named predicates over the served fixtures), SpatialEngine-imj (the
  grammar and the oracle), SpatialEngine-u2x.35 (the touches union),
  SpatialEngine-u2x.56 (the line/line crossings and overlaps), SpatialEngine-msc
  (a live ArcGIS Server), SpatialEngine-2ve (the operand direction)

## Measurements

NetTopologySuite 2.6.0, .NET 10, measured 2026-10-01.

**The three reports, each measured on the pair named.**

| report | what the engine reports | verdict |
| --- | --- | --- |
| a point at a line's endpoint reads disjoint | line (0,0)-(10,0) vs point (0,0) = `FF10F0FF2`; contact in position 4; `Intersects` true | not reproduced |
| a vertex contact reads as an edge | squares (0,0)-(10,10) vs (10,10)-(20,20) = `FF2F01212`; vs (10,0)-(20,10) = `FF2F11212` | not reproduced |
| the two paths report different cells | equal cell for cell over 256 ordered fixture pairs and 41,439 valid random pairs | not reproduced |

**The served table against the provider's named predicates**, over 256 ordered
pairs × 6 verbs = 1,536 checks: 16 divergences, all `Crosses`, in two classes.
`m` is the matrix with the feature on the left.

| pair (feature, query) | `m` | served | reference | class |
| --- | --- | --- | --- | --- |
| square, line (5,2)-(10,5)-(5,8) | `1020F1FF2` | **T** | **F** | line inside, touching the edge at its own interior vertex |
| square, line (0,0)-(5,0)-(5,5) | `102101FF2` | **T** | **F** | line along the edge, then inside |
| square, multi-line of the first line | `1020F1FF2` | **T** | **F** | as the first row, spelled as a collection |
| square, multi-point ((5,5),(20,20)) | `0F2FF10F2` | F | **T** | 0-D operand: no served mask |
| line (-5,5)-(15,5), multi-point | `0F1FF00F2` | F | **T** | 0-D operand: no served mask |
| multi-point, square | `0F0FFF212` | F | **T** | 0-D operand: no served mask |

The six area/line pairs are the first class and the ten point-involving pairs
are the second; the table names one of each because the classes are what a
reader needs, and the list is closed in the test.