---
status: accepted
date: 2026-10-02
deciders: maintainer + agent
summary: **Serve the mixed-dimension `Crosses` row as the OGC alternation, `T**T*****` / `T*T******`, and keep the divergence from the provider's own predicate deliberate rather than incidental.** For a line lying wholly inside an area and touching its boundary from inside this engine answers `Crosses` where NetTopologySuite and PostGIS do not, and the reason it does is that the alternation the table states is the contract this facade serves. The frame half of the pair is stated too — the same pair answers true with the area on the left and false with the line on the left, which follows from the left-operand frame rather than from a second rule.
amends: ADR-0106
related: ADR-0166, ADR-0156, ADR-0036
---

# ADR-0169: The mixed-dimension `Crosses` row stays the OGC alternation

## Context

The characterisation battery ADR-0166 built found that the served `spatialRel`
table and the provider's own named predicates disagree on exactly one verb,
`Crosses`, and in exactly two classes. The first is a line lying wholly inside
the closed area and touching its boundary from inside: the served mask
`T**T*****` asks that the interiors meet (position 1) and that the line's
interior reaches the area's boundary (position 4), both of which hold, so the
served answer is `true`. NetTopologySuite's `Crosses` also requires the area's
exterior to reach the line's interior — position 7 — which does not hold for a
line that never leaves, so the predicate answers `false`. Six of the 1,536
ordered-pair checks diverge this way, all the same shape.

ADR-0106 settled which pattern answers which verb and served the OGC table
verbatim, so this is not a defect by the decision already made. It is also not
nothing: the pair is one a client comparing this engine with ArcGIS or with JTS
meets, and "a line inside a polygon that touches its boundary crosses it" is a
claim a client will act on. ADR-0166 named the question open and pointed at a
live ArcGIS Server (SpatialEngine-msc) for the answer. Until that answer
arrives the pair is served on the strength of a mask nobody has argued about,
which is the state this record ends.

## Decision

**Serve the mixed-dimension `Crosses` row as the OGC alternation states it, and
make the divergence a decision rather than an artefact.**

1. **The alternation stands; no position-7 condition is added.** `T**T*****`
   (the feature is the surface) and `T*T******` (the query is) are what the
   served table asks, and a line inside an area touching its boundary is served
   `Crosses` where the provider says not. Three reasons, in the order they
   matter: the served contract is the OGC table, which is published in the
   compatibility reference and which a client can resolve this pair against
   without asking this server; the provider's extra condition is *not in the
   table* and belongs to JTS's composite predicate, which PostGIS shares, so
   adding it would make one implementation's rule the standard and would turn a
   NetTopologySuite version bump into a change to what every served relation
   answers (the alternative ADR-0166 rejected); and the interop evidence that
   would justify it does not exist yet — deciding the Esri surface on the
   GEOS lineage alone is deciding it on the wrong authority.
2. **The frame half of the pair is stated, not smoothed.** With the area on the
   left the pair is `Crosses`; with the line on the left the mask is
   `T*T******`, whose position 3 — the line's exterior against the area's
   interior — is `F` for this pair, so it is not. That asymmetry is a property
   of the frame ADR-0106 pins (feature on the left), not a second rule, and it
   is the strongest argument a future reader will have for the other answer.
   It is recorded here and pinned in the tests rather than left to be
   discovered by a client.
3. **The pair is pinned where a client reads the served table.** The served
   answer, the mask actually asked in each frame, both matrices and the
   provider's opposite verdict are asserted together, so a change to the mask
   is a failing test rather than a silent change to a served answer.

## Alternatives

- **Add the provider's condition — read the row strictly, as
  `T**T**T**`/`T*T**T***`.** It is the tempting answer: the served table would
  then agree with the provider on all 1,536 checks, the divergence list would
  fall from sixteen to the ten `MultiPoint` pairs, and a served answer would
  stop depending on which operand happens to be the feature. Rejected for the
  reason in decision 1: the condition is not the standard's, it is one
  lineage's, and adopting it would make the standard a function of which
  geometry library this repository pinned. If a live ArcGIS Server answers
  `false` (SpatialEngine-msc), the right response is a record that amends this
  one and changes the mask deliberately, not a mask that drifted into place
  while nobody was reading it.
- **Serve both readings and let the request choose.** There is one closed
  `spatialRel` grammar and no parameter that asks for a reading; adding one
  gives two answers to one question with nothing to choose between them, which
  is the same objection ADR-0106 raised against keeping two implementations.
- **Wait for the live-server measurement before deciding anything.** Rejected:
  the alternation is what is served now, so waiting changes no answer and
  leaves the sharp edge both undecided and undocumented. The pin is what makes
  a deferral safe, and this record is the decision to defer against evidence
  rather than against attention.

## Not decided

- **What a live ArcGIS Server answers** for a line inside a polygon touching its
  boundary (SpatialEngine-msc). That measurement, and only that, reopens this
  record; the change it would justify is the mixed-dimension mask plus six
  pairs leaving the closed divergence list in the characterisation battery.
- **The direction of the matrix's operands** (SpatialEngine-2ve), which owns
  why the pair answers one way in one frame and the other way in the other.
  The asymmetry stated in decision 2 is evidence for that bead, not a decision
  made here.

## Consequences

- **No served answer changes.** The pair keeps answering `Crosses` with the
  area on the left, and the divergence stays one of the sixteen named in the
  engine's characterisation suite and asserted closed.
- **A client sees the frame dependence in the place it looks.** The `relation`
  row of the GeoServices compatibility reference now says that this class of
  pair answers `true` with the surface in the left geometry's role and `false`
  with it in the right's, so a client asking the same question of a
  `geometries1`/`geometries2` pair knows the difference is the frame rather
  than discovering it as a bug.
- **The pin is the guard.** The served mask and this pair's verdict are
  asserted together in
  `tests/unit/Spatial.Adapter.GeoServices.Tests/SpatialRelationReadingTests.cs`,
  so a change to the mask — by this repository or by a provider upgrade that
  moves a cell — fails there rather than in a client's query.
- **Cost, stated:** for a line lying wholly inside an area and touching its
  boundary, this engine says `Crosses` and NetTopologySuite and PostGIS do
  not. That is the cost of serving a published table rather than a provider's
  predicate, it is one class of six pairs, and it is documented in two places a
  client can find rather than left to be measured.
- **Forbidden without a record:** moving a served mask to match a provider's
  named predicate. ADR-0106 and ADR-0166 both rejected that once; this record
  makes it a change that has to be argued.

## References

- ADR-0036 (the relation verbs and the pattern table), ADR-0106 (the served
  reading, the alternation, and the operand frame this record amends),
  ADR-0156 (the two independent readers of the table), ADR-0166 (the
  characterisation, the 1,536-check measurement and the divergence list),
  ADR-0053 (test-first)
- `src/Spatial.Adapter.GeoServices/SpatialRelationPredicates.cs`,
  `tests/unit/Spatial.Adapter.GeoServices.Tests/SpatialRelationReadingTests.cs`,
  `tests/unit/Spatial.Operations.NetTopologySuite.Tests/NtsRelateCellSemanticsTests.cs`,
  `architecture/references/geoservices-compatibility.md` (§2 `relation` row)
- SpatialEngine-7qk (this decision), SpatialEngine-aqy (the measurement),
  SpatialEngine-onj (ADR-0106), SpatialEngine-msc (a live ArcGIS Server),
  SpatialEngine-2ve (the operand direction)

## Measurements

NetTopologySuite 2.6.0, .NET 10, measured 2026-10-02. The line is (5,2)-(10,5)-
(5,8) against the unit square; it lies wholly inside and touches the boundary
at its own interior vertex.

| pair (feature, query) | `m` | served | provider |
| --- | --- | --- | --- |
| square, line | `1020F1FF2` | **T** (`T**T*****`) | F |
| line, square | `10F0FF212` | F (`T*T******`) | F |

Position 4 is non-empty and position 7 empty in the first frame — the contact is
there and the line never leaves. The second row is the transpose; the served mask
changes with the frame and answers false, because position 3 is `F` for this
pair.

**The pin, and what it fails.** `SpatialRelationReadingTests`, 49 cases, 0
failed. With `CrossesFeatureSurfacePattern` replaced by the strict reading
`T**T**T**`: 4 of 5,933 cases in the adapter suite fail, including the new pin
and the three that name the mask. With the same strict mask in the
characterisation suite's own table, `NtsRelateCellSemanticsTests` (3,588 cases)
fails on one case — `The_documented_divergences_are_exactly_the_measured_ones` —
because the first class stops diverging and the closed list no longer matches.
That is the whole evidence for the other answer: it is one mask and one list,
and it works.
