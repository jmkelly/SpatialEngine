---
status: accepted
date: 2026-10-08
deciders: maintainer + agent
summary: NADCON (.las/.los) grids are read alongside NTv2, behind the same DatumShiftGrid, so a deployed North American pair reaches the same graph and the published operation names the standard that served it (amends 0105).
amends: ADR-0105
amended-by: ADR-0180, ADR-0181
---

# ADR-0168: NADCON datum shift grids are read alongside NTv2, and the standard is published with the operation

## Context

ADR-0105 §1 read NTv2 and declined NADCON on purpose. Its reason was not
that NADCON is harder: it is that NADCON is a **different container with
different semantics**, and a binary parser written with nothing to check it
against is a parser nobody can check.

NADCON's container is a *pair*. A `.gsb` bundle holds one block per sub-grid
with all four of a node's values in it; a NADCON deployment is a `.las` file
and a `.los` file with identical headers, the first carrying the latitude
shifts and the second the longitude shifts, one single-precision value per
node. Nothing downstream of the registry — the graph, the candidate, the
published operation — knows or needs to know that: all of them already speak
`DatumShiftGrid`, which is a block of ground with shifts over it. So the
reader is the whole of the difference, plus one thing the table of grid
operations has to be able to say.

What blocks the check ADR-0105 asked for is ADR-0105 §licence: no published
grid bundle is vendored, embedded or downloaded by the engine or its tests, so
there is no NADCON file on any machine this was written on to compare a field
width against. That constraint is not up for renegotiation here — it is the
licence position — so what the reader can be pinned by is this repository's own
fixture, and what has to be stated rather than assumed is that the byte layout
is *this engine's reading* of the container.

## Decision

1. **NADCON is read, into the same `DatumShiftGrid` everything else speaks.**
   `NadconGridReader` reads a `.las`/`.los` pair and produces the block, the
   coverage, the shifts and the format behind one value, so the registry, the
   graph and the published operation are unchanged by which standard served a
   datum. The NTv2 reader is untouched, and a host that deploys only NTv2
   bundles behaves exactly as it did.

2. **The two halves are joined, and cross-checked against each other.**
   A pair is one grid: the latitude shifts come from the `.las`, the longitude
   shifts from the `.los`, and both halves' headers must describe the same
   block, the same lattice and the same sub-grid or neither is read. Half a
   pair deployed is not a grid either — the registry refuses it and says which
   of the two files is missing, rather than answering a longitude shift with
   no latitude shift beside it.

3. **A grid operation row states its container, and a datum may have more than
   one row.** `GridShiftOperation` gains an optional `.los` file name and an
   optional accuracy: a row naming its own second file is a NADCON pair, and a
   row that does not is a single NTv2 bundle. Which reader serves a row is
   stated by the row and never guessed from a file extension. `For` now returns
   every row a datum has, and the registry tries them in catalogue order within
   the first directory holding any of them, so an operator may deploy an NTv2
   file or a NADCON pair for the same datum and the priority order still holds.

4. **A NADCON grid's accuracy is the figure its row publishes.** ADR-0105 §9
   keeps the accuracy out of the grid-operation table because an NTv2 bundle
   states it per node and the worst node is the authority, so a second number
   beside the file name could disagree with the file. A NADCON shift record
   holds the shift alone: there is no per-node accuracy in it and therefore no
   worst node to read, and the row's number is the only claim about it rather
   than a competing one. It is stated on the row because a grid whose accuracy
   nothing states would be published as exact.

5. **The published operation names the standard, and nothing else does.**
   `GridFormat.Nadcon` and `GridFormats.Standard` give the spelling `NADCON`,
   the one a client reading a `findTransformations` listing wants beside a file
   it has never heard of. The method text is generated from that spelling
   rather than naming NTv2, and the Helmert candidate's "no grid is deployed"
   fallback text no longer claims the grid that is missing would have been an
   NTv2 one.

6. **The refusal rules are NTv2's, applied harder.** A NADCON header states
   its block three times over — as edges with increments, as node counts, and
   as the corner the increments run from — and the reader reads all three and
   requires them to agree. Rows are stored north-to-south and normalised to
   south-first as they are read, exactly as the NTv2 reader does, because a
   block read the wrong way up shifts every coordinate in it by the wrong
   amount. The block's numbers are read *as numbers* rather than into fixed
   columns, because which records a header spends on identity, source and units
   is a detail of the file.

## Alternatives

- **Leave NADCON unread, as ADR-0105 §1 did.** Rejected for this slice
  because the objection was checkability, not difficulty: the container is
  fully specified here, the reader refuses everything it cannot account for,
  and a bundle it cannot read falls back to the Helmert and says so rather than
  being half-applied. ADR-0105's own wording promised the format would be
  published per operation "so a client is never left guessing", and a client
  over North American ground is exactly the one left guessing.
- **Read a NADCON pair as though it were an NTv2 bundle** — one reader, one
  format, no new row shape. Rejected: the two containers genuinely differ (two
  files, four bytes per node, no per-node accuracy), so the pretending would
  live somewhere, and it would live in a format enum that claimed NTv2 for a
  file that is not one.
- **Widen the ADR-0105 §licence position to vendor or fetch a NADCON bundle
  for the tests.** Rejected outright: that is a licence decision that belongs
  to whoever holds the licence, it is already filed as SpatialEngine-yt2, and
  this record does not get to make it.
- **Publish a NADCON row for the North American datums straight away.**
  Rejected, and filed as its own bead (SpatialEngine-jy1's follow-on). No
  datum this catalogue serves has a NADCON-registered grid operation — NAD83's
  registered operation against WGS 84 is the null one ADR-0163 publishes — so
  a row would publish a claim about which file serves which datum that no
  record in the EPSG Geodetic Parameter Dataset supports.

## Not decided

- **The byte layout is verified against nothing but this repository's own
  fixture.** A real NADCON bundle's header may state its numbers in a different
  order, in a different set of records, or with a different terminator; if it
  does, this reader refuses it with the reason and the Helmert stands. What
  settles it is a published bundle read against the agency's documentation,
  which is SpatialEngine-yt2's deployment-and-licence exercise and is not a
  code change this record can make.
- **Which NADCON rows the catalogue publishes.** NADCON serves NAD27 against
  NAD83 and the NAD83 realizations against each other, and none of those datums
  is in the served catalogue. Whether to catalogue one is a question about the
  vendored WKT and the EPSG records for it, not about this reader.
- **NADCON5's own variants.** NADCON5 ships as the same `.las`/`.los` pair with
  a different shift encoding in some releases. This reader reads one encoding.
  What settles it is a bundle to compare against.

## Consequences

- A host that deploys a NADCON pair for a datum the catalogue names it for
  serves the shift through exactly the same graph, the same per-coordinate
  choice and the same published parameters as an NTv2 bundle, and a client
  reading the listing can tell which standard answered.
- The catalogue's grid rows gain two optional fields. A row that names a
  `.los` file and no accuracy is refused with a reason rather than published
  without one, so the two cannot be set inconsistently by accident.
- `For` returns a list where it returned a row, which every caller reads
  through the registry's own resolution — one caller, in this assembly.
- **The cost is the whole of this slice's assurance.** Nothing in the tree has
  read a NADCON file that NOAA published, so a deployed pair is likely to need
  a fix to this reader before it serves a coordinate. The refusal is loud and
  the fallback is the Helmert ADR-0105 already specifies, so the failure mode
  is a missing accuracy rather than a wrong shift — but a reader nobody can
  check is still only as good as its fixture, and ADR-0105 §1's objection to
  shipping one unread has been answered by refusing everything it cannot
  account for rather than by a comparison.
- Redeploying a grid is still a restart (ADR-0105 §4), for a pair as much as a
  bundle, and the licence position is untouched: nothing is vendored, fetched
  or redistributed.

## References

- ADR-0105 — datum shift grids are deployed, not embedded; §1 read NTv2 only,
  §9 derives a grid's accuracy from its worst node, §licence keeps published
  bundles out of the engine and its tests. This record amends §1 and carves one
  exception out of §9's reasoning.
- ADR-0107 — the transform verb applies a deployed grid per coordinate; its
  §not measured said only NTv2 was read, which this record makes out of date.
- ADR-0111, ADR-0163 — the area arithmetic and the published null operation
  that stands over NAD83.
- `src/Spatial.Transformations.ProjNet/Grids/NadconGridReader.cs`,
  `GridFormat.cs`, `DatumShiftGridRegistry.cs`,
  `src/Spatial.Transformations.ProjNet/EpsgGridShiftOperations.cs`,
  `Grids/GridShiftCandidate.cs`,
  `tests/unit/Spatial.Transformations.ProjNet.Tests/NadconFixture.cs`,
  `NadconGridReaderTests.cs`, `DatumShiftGridRegistryTests.cs`,
  `GridShiftGraphTests.cs`.
- SpatialEngine-jy1 (this bead), SpatialEngine-u2x.18 (the registry),
  SpatialEngine-yt2 (agreement with PROJ over published bundles).