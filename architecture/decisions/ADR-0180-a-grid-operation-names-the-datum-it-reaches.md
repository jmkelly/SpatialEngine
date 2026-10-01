---
status: accepted
date: 2026-10-08
deciders: maintainer + agent
summary: NAD27 is catalogued and its NADCON pair published as EPSG:1241 names it, and a grid operation now names the datum its shifts land in rather than assuming every bundle reaches WGS 84 (amends 0105, 0168).
amends: ADR-0105, ADR-0168
---

# ADR-0180: A grid operation names the datum it reaches, and NAD27 is the datum NADCON is registered against

## Context

ADR-0168 landed the NADCON reader and published no row for it, for a reason
that was a statement about the data rather than about the reader: no datum the
catalogue served had a NADCON-registered grid operation. That is now true of
every datum but one. EPSG registers NADCON — method EPSG:9613 — for **NAD27 to
NAD83** (EPSG:1241, the CONUS pair `conus.las` / `conus.los`, at 0.15 m;
EPSG:1243, the Alaska pair at 0.5 m), and for the NAD83 realizations against
each other. NAD27 is not in the catalogue, so there was no node for a row to
join to and the graph had nothing to publish.

Behind that sat a second thing, and it was the more interesting one.
`GridShiftOperation` carried a `TargetDatumCode`, and **nothing read it**: the
candidate builder and the transform plan both assumed every grid lands on the
pivot, so every grid leg was composed as though it reached it. For an NTv2 row
that assumption is true, and it is why the field could go unread for so long.
For a NADCON row it is false — the file takes NAD27 to NAD83 — so the shape
could not be settled by adding a row.

## Decision

1. **NAD27 (EPSG:4267) joins the catalogue**, as the other six datums do: the
   vendored EPSG WKT2 document with a `TOWGS84` node, and a row in
   `EpsgDatumOperations` citing the operation its accuracy and bounds were read
   from. That operation is **EPSG:1173 "NAD27 to WGS 84 (4)"** — three
   translations of -8, 160 and 176 metres, over the USA - CONUS - onshore, at
   10.0 m. It is the honest grid-free approximation, and it is also the region
   the conus grid is registered over. EPSG registers twenty-eight NAD27 to
   WGS 84 Helmerts, one per region, none better than 10 m over the whole
   country; a datum carries one area of use, so a row that wanted to cover
   Alaska as well would be restating one operation's accuracy over another's
   ground.

2. **A grid operation row names the datum its shifts land in, and the row's
   `TargetDatumCode` is read.** It is the EPSG coordinate reference system the
   cited operation states as its target CRS, so a row's claim about a file can
   be checked against the registry: EPSG:1241's target CRS is 4269, and the row
   says 4269. A grid leg is named for that datum rather than for the pivot, and
   the path continues from it along that datum's own registered path.

3. **The leg behind a grid is published, whether or not it moves.** For NAD27
   that leg is NAD83's registered null operation (ADR-0163), published as a step
   with the three zero translations the registry carries and priced at the
   4.0 m it states. An accuracy with no operation behind it is a number nobody
   published, and the composed path is then what the two records say: 0.15 m
   from EPSG:1241 and 4.0 m from EPSG:1188, in quadrature, as ADR-0087 combines
   two legs. The verb applies the same leg, which moves a point by the
   millimetres ADR-0163 already records rather than by nothing.

4. **A row naming a datum the catalogue does not serve is not published at
   all.** The search falls back to the Helmert candidates, which say so
   themselves, rather than composing a path from a node that does not exist, and
   the transform verb declines the same row the same way.

5. **The published row is EPSG:1241**: `conus.las` and `conus.los`, the
   accuracy it states, and the extent it is registered over (2374) as a
   cross-check on the block rather than a second claim about it. It is tried by
   deploying the two files into a configured grid directory, which is the whole
   of what deploying a grid is (ADR-0105); nothing is vendored, fetched or
   redistributed, and agreement with PROJ over a published bundle is
   SpatialEngine-yt2's exercise.

## Alternatives

- **Publish nothing, as ADR-0168 did.** Rejected on the evidence: the registry
  registers this operation, names both files and states an accuracy, so the
  honest answer is a row and not silence.
- **Publish the row as though the pair reached WGS 84**, keeping the table
  keyed on the pivot and reading nothing. Rejected: it publishes a claim about
  which file serves which datum that no record supports, and it would publish
  0.15 m for a path the registry states is worth 4.0 m. An invented target is
  the same defect as an invented file name.
- **Publish EPSG:15851 "NAD27 to WGS 84 (79)" instead** — the conus pair
  restated against the pivot, which fits the existing pivot-keyed table with no
  shape change at all, and is the same kind of operation as the Alaska one
  (15864). Rejected as the conservative option that costs the better operation:
  both are 5.0 m where the registry publishes 15 cm for the pair, and their own
  descriptions give the reason — *"Parameter files are from NAD27 to NAD83 (1)
  (code 1241) assuming that NAD83 is equivalent to WGS 84 within the accuracy
  of this tfm"*. That assumption is how the registry states a pivot-registered
  NADCON row: the leg behind the grid is treated as free. This engine does not
  treat it as free, because the registry states what it is worth — 4.0 m, at
  EPSG:1188 — and a candidate that claimed 5.0 m for a path the search prices
  elsewhere at 4.003 m would be two answers to one question. Neither row is
  lost: each is a second row over the shape this record settles.
- **Publish both pairs and give NAD27 its whole area of use** (extent 1349).
  Rejected: the accuracy would come from 1173's record and the area from the
  datum's usage record, the mixed claim every row here is written to avoid.
- **Key the table on a `From`/`To` pair rather than on a datum and the datum
  its grid reaches.** Rejected: `TargetDatumCode` already says it, in the form
  EPSG states on the operation, so a second field would restate one datum twice.

## Not decided

- **The sign of a NADCON longitude shift.** EPSG's note on method 9613 states
  the method "expects longitudes positive west", and PROJ negates the `.los`
  values when it reads one (`NTv1Grid::valueAt`, 9.8.1). This repository's
  reader adds them, because it was written against its own fixture and
  ADR-0168 §not-decided says so. Whether a pair NOAA published reads that way
  is settled by nothing in this tree; it is filed against the reader, not
  against this table. Until then a deployed pair may shift longitude the wrong
  way — the one failure mode ADR-0105's refusal rules do not catch, because the
  file is read perfectly and the sign is in the convention, not in the bytes.
- **The Alaska pair, and the island pairs.** EPSG:1243 is the same kind of row
  and the same shape carries it, but a datum carries one registered area of use
  and NAD27's is now the conus, so publishing it needs a decision about a
  datum's area that this record does not make. The other NADCON pairs this
  catalogue could serve are not NAD27's at all: `hawaii.las`, `prvi.las`,
  `stgeorge.las`, `stlrnc.las` and `stpaul.las` are registered by EPSG:1454,
  1461, 1457, 1455 and 1456 against **Old Hawaiian, Puerto Rico, St. George
  Island, St. Lawrence Island and St. Paul Island** — five further datums,
  each with its own Helmert row before it has a grid row.
- **Whether a deployed NADCON pair agrees with PROJ.** That is
  SpatialEngine-yt2's exercise, and not a code change this record can make.

## Consequences

- A client can ask for EPSG:4267 and get an answer: the registered Helmert
  path, or the NADCON pair where one is deployed, ranked ahead of it at its own
  accuracy and with the null operation behind it published and priced.
- The grid table's target field is no longer a claim nobody checks, and the
  tests hold each row against the record it was transcribed from.
- The published candidate for a NADCON pair costs 4.0 m rather than 0.15 m, and
  says why in a second step and in the method text. A client reading only the
  grid's own figure was reading a number the registry does not state for the
  path it asked about.
- The transform plan gained one ProjNet pairing per request, built once with
  the plan: the shift from the datum a grid lands on to the pivot. For NAD83
  that is a zero-parameter geocentric round trip, which is what ADR-0163 already
  does for every other registered null pair.
- NAD27 is now a served core CRS, and deliberately **not** a WKID: the curated
  WKID map is the Esri surface's own table, and a code the engine can serve
  through a CRS identity need not be an Esri spatial reference.
- The cost is the reader's, not this table's: ADR-0168's assurance is still
  exactly one fixture, and this record adds a published operation that depends
  on it. The sign question above is the known instance.

## References

- ADR-0105 §licence (nothing vendored or fetched), §9 (accuracy derived from
  the grid), and the deployment model this record uses; ADR-0107 (the transform
  verb applies a grid per coordinate); ADR-0086 (a datum's parameters live in
  its definition and nowhere else); ADR-0087 (accuracies combine in quadrature);
  ADR-0111 (area of use); ADR-0163 (the registered null operation, its 4.0 m,
  and the geocentric round trip's millimetres); ADR-0168 §3 (a row states its
  container) and §not-decided (the layout is verified against this repository's
  fixture only).
- `src/Spatial.Transformations.ProjNet/EpsgGridShiftOperations.cs` — the row
  and the field that is read; `EpsgWktDefinitions.cs` — the vendored EPSG:4267
  document; `EpsgDatumOperations.cs` — the EPSG:1173 row; `Grids/DatumShiftGridRegistry.cs` —
  the operation served with the grid; `Grids/GridShiftCandidate.cs` — the grid
  leg and the leg behind it; `Grids/GridShiftPlan.cs` — the same leg applied.
- `tests/unit/Spatial.Transformations.ProjNet.Tests/NadconGridOperationTests.cs`
  — the decision, pinned, including the published names, the composed accuracy
  and the transform's answer inside and outside the block.
- SpatialEngine-1do (this bead), SpatialEngine-jy1 (the reader and the row it
  did not add), SpatialEngine-yt2 (agreement with PROJ over published bundles).

## Measurements

Read from the EPSG Geodetic Parameter Dataset as carried by PROJ 9.8.1
(`proj.db`, EPSG v12.029) — the same release every row in `EpsgDatumOperations`
was transcribed from — queried on 2026-10-08.

| Record | Says | Value used here |
| --- | --- | --- |
| EPSG:1241 "NAD27 to NAD83 (1)" | method 9613 NADCON, source 4267, target 4269, accuracy 0.15 m, grids `conus.las` / `conus.los`, version "NGS-Usa Conus" | the published row: both file names, the target CRS, the accuracy |
| extent 2374 "USA - CONUS including EEZ" | 23.81N–49.38N, 129.17W–65.69W | the row's cross-check on the block |
| EPSG:1243 "NAD27 to NAD83 (2)" | NADCON, `alaska.las` / `alaska.los`, accuracy 0.5 m, extent 2373 | not published; see Not decided |
| EPSG:15851 "NAD27 to WGS 84 (79)" | NADCON, `conus.las` / `conus.los`, accuracy 5.0 m, extent 2374; "assuming that NAD83 is equivalent to WGS 84 within the accuracy of this tfm" | rejected in favour of 1241; its own assumption is the decision |
| EPSG:15864 "NAD27 to WGS 84 (85)" | NADCON, `alaska.las` / `alaska.los`, accuracy 5.0 m, extent 2373 | not published; see Not decided |
| EPSG:1173 "NAD27 to WGS 84 (4)" | 9603 geocentric translations, (-8, 160, 176), accuracy 10.0 m, extent 1323 "USA - CONUS - onshore" | the datum row and the vendored `TOWGS84` node |
| EPSG:1188 "NAD83 to WGS 84 (1)" | 9603, all parameters zero, accuracy 4.0 m | the leg behind the grid, published by ADR-0163 |

The composed accuracy this record publishes for a conus pair is
`sqrt(0.15² + 4.0²) = 4.003 m`. EPSG states 5.0 m for its own NAD27-to-WGS-84
NADCON operation over the conus (15851), and says in the same record that it
reaches WGS 84 by *assuming* NAD83 is equivalent to it. This record does not
make that assumption: it composes the pair with the operation the registry
registers for the leg behind it. The combination rule is ADR-0087's and both of
its inputs are EPSG's.
