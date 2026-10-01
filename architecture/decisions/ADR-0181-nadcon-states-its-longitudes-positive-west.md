---
status: accepted
date: 2026-10-08
deciders: maintainer + agent
summary: The NADCON reader converts the container's positive-west longitudes — the header's edges and the .los half's shifts — into the datums' positive-east convention as the pair is read, so a deployed North American grid shifts a coordinate the way the file tabulates it (amends 0168).
amends: ADR-0168
related: ADR-0105, ADR-0180
---

# ADR-0181: A NADCON deployment states its longitudes positive west, and the reader is where that is answered

## Context

ADR-0168 landed a reader for the NADCON `.las`/`.los` pair behind the same
`DatumShiftGrid` everything else speaks, and ADR-0180 published the first row
that deploys one. Both were correct about the container's *shape* — two files,
one header each, the `.las` carrying latitude shifts and the `.los` longitude
shifts, four bytes a node — and neither asked what sign the container keeps
its longitudes in. It is not the datums'.

NADCON's method was built for **NAD27**, whose longitudes are positive west.
The datums a NADCON grid reaches are positive east. EPSG states the gap on
the operations themselves: operations **1241** ("NAD27 to NAD83 (1)"), **1243**
and **15864** each carry the note *"Uses NADCON method which expects longitudes
positive west; EPSG Geodetic Parameter Dataset NAD27 (code 4267) and NAD83
(code 4269) have longitudes positive east."* PROJ 9.8.1 does the conversion in
two places, `src/grids.cpp`: `NTv1Grid::valueAt` multiplies the longitude
shift by `-1` when `compensateNTConvention` is set, which it is for the
`nadcon` file type, and the header's `extent.west`/`extent.east` are negated
for the same reason.

ADR-0168's reader did neither, so a deployed pair shifted longitude the wrong
way by the whole of its tabulated value — of the order of a metre — and read
its block's eastern and western edges off the wrong side of the prime
meridian. Nothing above the reader would have caught it: the file is read
perfectly, the block is the right shape, the pair cross-checks, and the
refusal rules ADR-0105 §6 and ADR-0168 §2 depend on are all satisfied. The
convention is in the semantics, not in the bytes. That makes this the one
failure mode the licence-driven refusal discipline of ADR-0168 §6 does not
cover, and it was a wrong coordinate rather than a missing one.

It was found while publishing the NAD27 rows (SpatialEngine-1do) and not fixed
there: that bead's working set was the catalogue, and a reader defect in a
landed slice wants its own record and its own review.

## Decision

1. **The reader converts, and nothing downstream of it knows a NADCON grid
   exists.** Every longitude a NADCON deployment states is positive west — the
   block's two edges, the corner its increments are measured from, and the
   shifts the `.los` half tabulates — and `NadconGridReader` negates all of
   them as it reads them. The `DatumShiftGrid` a pair produces is tabulated in
   the convention of the datum it reaches, which is the one the registry, the
   graph, the candidate and the published operation already speak. `.las`
   values are positive north and are not touched.

2. **The conversion is in the reader, not keyed on `GridFormat` downstream.**
   ADR-0168 §1 put the whole of the difference between the two containers in
   this class; a `GridFormat.Nadcon` branch in `DatumShiftGrid` would put half
   of it back and leave the next container's convention to be remembered by
   whoever reads it. The header's longitudes and the `.los` values are negated
   in the same reader, for the same reason, and the fixture writes both the
   way a deployment holds them so that the two cannot drift apart.

3. **The fixture states the container's numbers, and the tests state the
   answers in the datums' degrees.** `NadconFixture.Grid.Shifts` is what the
   file tabulates — positive west in the `.los` half — and the expected
   coordinates the tests assert are east-positive degrees. A fixture written
   in the datums' own convention would have pinned the reader against a file
   NADCON does not publish, which is the mistake this record exists to undo.

## Alternatives

- **Negate in `DatumShiftGrid`, keyed on `GridFormat`.** Rejected: it puts the
  container's semantics in the value every grid shares, so a format the enum
  grows later carries its convention wherever it is used rather than in the one
  place that reads the container.
- **Negate in the fixture's tests only, leaving the reader as it is.** Rejected
  as what it is: a test that asserts the wrong sign is a test that will be
  deleted by whoever deploys a bundle and gets a wrong answer (ADR-0181's own
  finding). The reader is where the file's convention is known.
- **Read the container's sign and let the shift be applied as stored, treating
  positive west as an ordinary sign convention.** Rejected — it is the defect.
  The engine's coordinates are east-positive because the CRSs are
  (ADR-0086), and the datum's own convention is not a candidate scheme here.
- **Compare against a published bundle before believing this.** Not an
  alternative to the fix but the exercise that confirms it, and it is not one
  this repository may run: ADR-0105 §licence keeps a published bundle out of
  the tree and out of its tests, and the comparison is SpatialEngine-yt2's,
  operator-run deployment exercise (ADR-0179).

## Not decided

- **Whether NADCON5's `.los` uses the same convention.** It ships as the same
  pair with a different shift encoding in some releases. This reader reads one
  encoding and one convention; what settles the other is a bundle read against
  the agency's documentation, which is SpatialEngine-yt2's exercise.
- **The record order and terminator of a real sub-grid header.** Untouched by
  this record: ADR-0168 reads the numbers as numbers, so the layout question is
  the one already filed as yt2's, and this only fixes the sign of what is read
  once the header has been read.

## Consequences

- A deployed NADCON pair now moves a coordinate the way the file tabulates it,
  which is the thing a `.los` holding `+1.5` has always meant. Every NADCON
  grid read before this record was moving longitude the wrong way by its whole
  shift, and a host that deployed one in the field got wrong coordinates
  silently — the cost of the previous record's assurance, stated plainly in ADR-0168
  §consequences, has now been paid once.
- The block a header describes is read east-positive, so a NADCON grid covers
  the ground the file covers. Before this, a North American header — which
  states its edges as positive-west magnitudes — described a block on the far
  side of the Atlantic, where no point of the grid it was asked about could
  fall inside it, and the engine would have fallen back to the Helmert rather
  than applied a wrong shift.
- The fixture and the reader now have to agree about the container's
  convention, and the tests are where that is checked: a fixture that went
  back to east-positive values would fail the two tests named for the
  convention, and a reader that dropped the negation would fail them too.
- Nothing outside this reader changed. `DatumShiftGrid`, the registry, the
  graph, the catalogue row and the published parameters are exactly as
  ADR-0180 left them, and an NTv2 deployment is untouched.

## References

- ADR-0105 — grids are deployed, not embedded; §6's refusal rules are what
  this defect was *not* caught by; §licence keeps a published bundle out of
  the engine and its tests.
- ADR-0168 — the NADCON reader, whose §1 put the whole difference between the
  containers in `NadconGridReader` and whose consequences named the unverified
  byte layout as this slice's cost. This record answers the half of that cost
  the bytes alone could not.
- ADR-0180 — published the NAD27 row that deploys a pair, and is where the
  defect was found.
- ADR-0179 — the published-bundle comparison is an operator-run exercise, not
  a CI gate.
- `src/Spatial.Transformations.ProjNet/Grids/NadconGridReader.cs`,
  `tests/unit/Spatial.Transformations.ProjNet.Tests/NadconFixture.cs`,
  `NadconGridReaderTests.cs`.
- SpatialEngine-90n (this bead), SpatialEngine-1do (found while publishing the
  rows), SpatialEngine-yt2 (published-bundle agreement, still open).

## Measurements

- The reproduction, before and after. A pair whose `.los` holds `+1.5` at
  every node over 24–26 °N, 84–82 °W, read by `NadconGridReader` and asked to
  shift (−83.5, 25.5) — `dotnet test --filter FullyQualifiedName~NadconGridReaderTests`,
  2026-10-08:

  | | before | after |
  |---|---|---|
  | block `XMin`/`XMax` | −82.0 / −84.0 (normalised, wrong side) | −84.0 / −82.0 |
  | longitude shift, arc-seconds | +1.5 | −1.5 |
  | latitude shift, arc-seconds | +1.5 | +1.5 |

  One and a half seconds of arc is about forty metres of longitude at that
  latitude, so the two answers differ by about eighty metres on a point the
  grid covers.
