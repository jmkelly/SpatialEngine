---
status: accepted
date: 2026-10-01
deciders: maintainer + agent
amends: ADR-0172
related: ADR-0027, ADR-0086, ADR-0170
summary: The ESRI WKT1 spelling of Hotine Oblique Mercator (variant A) resolves to the same projection as the EPSG spelling and reads the skew grid angle it does not state as the azimuth of the initial line, PROJ's own reading of that dialect; the refusal of a document that states the angle anyway is gated on that spelling alone, so an EPSG variant A definition is still read exactly as it states (ADR-0172).
---

# ADR-0174: The ESRI WKT1 Hotine variant A takes its skew angle from its azimuth

## Context

ADR-0172 put EPSG method 9812 in the method map and resolved it to ProjNet's
`Oblique_Mercator`, the reading that applies a variant A's false offsets at
the natural origin. It deliberately left one thing open in its *Not decided*
section: **the ESRI WKT1 spelling** of the same method,
`Hotine_Oblique_Mercator_Azimuth_Natural_Origin`, which is what `projinfo -o
WKT1_ESRI` emits for EPSG:3078. That spelling was out of the map on purpose,
and the reason was a parameter rather than the method.

The ESRI WKT1 dialect's oblique Mercator states the azimuth of the initial
line and stops there. It has no parameter for the angle from the rectified to
the skew grid — EPSG's parameter 8814 — which EPSG's own registry states for
the same grid as 337.25556, the same number as the azimuth. So a reader that
mapped the spelling and read no angle would hand ProjNet a `rectified_grid_angle`
of zero: not a default but a different grid, 2,046,891 m of easting on the
Michigan projection centre, and every other point of the definition with it.
Mapping it, reading no angle and serving the result is a parameter accepted
and ignored, which this repository does not do; leaving it out of the map was
the honest refusal.

Two beads then claimed the same method. ADR-0172 (SpatialEngine-r4o) put the
EPSG spelling in the map and pinned four grids to 1e-6 m; SpatialEngine-9r3
wrote the ESRI half on a branch taken before that landed, and the two branches
would not rebase together. They are the same method and the same ProjNet
class, so a union of the two map rows compiles — but each had a premise about
what the other did not know. r4o's premise ("the ESRI spelling is deliberately
not in the map today") was true at branch-off and false on main, and 9r3's
worked only because the ESRI spelling was absent, so *any* 9812 document that
omitted the angle had to be refused rather than served. The two claims meet on
one question nobody had written down: **what does the engine do with a method
9812 document that does not state a skew angle, and is that keyed on the
method or on the dialect?**

## Decision

**Key the ESRI skew-angle convention on the ESRI WKT1 spelling, and leave
every other variant A document read exactly as it states.** The two spellings
are a deliberate pair, pinned by a test each.

1. **Both spellings resolve to `Oblique_Mercator`.** ADR-0172's resolution is
   unchanged: the ESRI name is an abbreviation of EPSG's, not a different
   projection, so it maps to the same class and inherits the same
   natural-origin reading of the false offsets.

2. **On the ESRI spelling, the skew grid angle is the azimuth of the initial
   line.** This is PROJ's own reading of the dialect — `+gamma` defaults to
   `+alpha` in `+proj=omerc`, and PROJ 9.8.1 imports the WKT1 document for
   EPSG:3078 to `+alpha=-22.74444 +gamma=-22.74444` — so the ESRI document
   lands on PROJ's coordinates and agrees with the WKT2 of the same grid.

3. **On the ESRI spelling, a document that states a skew angle anyway is
   refused by name.** PROJ 9.8.1 reads that parameter and then ignores it
   (it imports such a document to the same proj string, `+gamma` equal to
   `+alpha` either way), so honouring it and honouring the convention would be
   two different grids under one name. The engine picks neither silently: the
   document is refused by name, which is what "either honoured or rejected"
   costs when a parameter's two readings disagree.

4. **On every other spelling of 9812, nothing changes.** An EPSG variant A
   definition states the angle, and ADR-0172's second clause — no parameter is
   derived, translated or moved — reads it as stated. It is not refused for
   stating it, and it is not overwritten by the azimuth. A document that
   states an angle *different* from its azimuth is served on the angle it
   states, because that is the document that describes its own grid; the ESRI
   convention is not applied behind the EPSG dialect's back.

5. **The gate is the spelling, not the method, and it is named.** The reader
   carries one flag on the resolved method, set from the normalised method
   name, and it is the same name the map entry is keyed on. A future variant A
   spelling that also omits the angle has to be added to the map and to the
   flag deliberately, and the pair test says so.

## Alternatives

- **Infer the angle from the azimuth for *any* 9812 document that omits it, and
  refuse any 9812 document that states one** — uniform on the method rather
  than the dialect. Rejected: it would refuse the EPSG WKT2 of every variant A
  grid, because EPSG's dialect always states the angle. That is not a small
  change to ADR-0172, it is the withdrawal of it: the four grids it pinned
  (Michigan, Sarawak LSD, Oregon Coast, Alaska zone 1) would all become
  unreadable, and its clause 2 — every parameter read as the document states
  it — would be inverted into clause 2's opposite. Uniformity is not worth
  un-serving the grids, and the asymmetry is a property of the dialects, not a
  gap in the reader.
- **Infer the angle for any 9812 document that omits it, and read a stated one
  as stated** — uniform in what is *supplied*, and no refusal at all.
  Rejected on the same ground the refusal exists: the ESRI dialect's documents
  have no angle, so nothing is refused on that spelling, but a document that
  states one and is served on it is being told it is the azimuth when it is
  not, and PROJ disagrees with the engine on exactly that document.
- **Refuse the ESRI spelling by name and leave it out of the map** — ADR-0172's
  status quo, which ADR-0172 itself called a refusal that serves nothing. The
  dialect is what a caller with a published ESRI definition has, and the
  convention that reads it is PROJ's own, so the gap is a missing convention
  rather than a wrong projection.
- **Infer the angle on the ESRI spelling and refuse a document that states one
  only when the two disagree** — a narrower refusal, which reads a document
  that states the angle the same number as the azimuth. Rejected: it makes the
  outcome depend on a coincidence of numbers rather than on the dialect, and
  the two branches of it (equal or not) are not a distinction a caller can
  act on without reading the reader.

## Not decided

- **Whether the engine should name a definition's variant or dialect to a
  caller.** The map is unambiguous and this record says so; nothing yet asks
  the catalogue to label how a definition arrived.
- **Whether a *published* ESRI oblique Mercator definition can carry a skew
  angle.** If ESRI ever adds parameter 8814 to the dialect, this refusal stops
  being right, and PROJ's importer is the thing that would move first. The
  measurement here is PROJ 9.8.1's, dated, and re-checking it is what settles
  it.

## Consequences

- **The map holds ten measured methods and one spelling more**, and both
  spellings of 9812 land on the same coordinates, to the fourth decimal of a
  metre — which is the difference between EPSG's six-decimal false offsets as
  the ESRI dialect states them and the extra digits its registry gives, and
  not a disagreement between the projections.
- **The cost is an asymmetry in the reader that has to be defended rather than
  rediscovered.** One flag on the resolved method gates both the supply and the
  refusal, so a future edit that widens it widens both; the pair tests
  (`The_ESRI_convention_does_not_reach_a_document_that_states_its_own_angle`
  and `The_supply_and_the_refusal_are_the_ESRI_spelling_alone`) exist so that
  widening one half of it fails a test.
- **A refusal is now reachable for a method the reader otherwise serves.** An
  ESRI WKT1 oblique Mercator stating an angle is refused where it used to be
  read as an unknown parameter — the error names the parameter either way, but
  the reason is now the dialect's, and that reason is in the message.

## References

- ADR-0027 §2 (the method map, and the rule that a method is in it only where
  it is measured to agree), ADR-0086 (the catalogue is WKT read by a reader of
  our own), ADR-0170 (the sibling repair that was a refusal), ADR-0172 (the
  variant A resolution this record amends, and whose *Not decided* section
  filed this one)
- SpatialEngine-9r3 (the ESRI half, written on a branch that would not rebase;
  superseded and re-landed here), SpatialEngine-r4o (ADR-0172), SpatialEngine-u2x.62
  (the escalation and this record)
- `src/Spatial.Transformations.ProjNet/ProjWkt.cs` — the `Projections` map, its
  method table, `ResolvedMethod` and `AzimuthIsTheSkewGridAngle`
- `tests/unit/Spatial.Transformations.ProjNet.Tests/ProjWktEsriObliqueMercatorTests.cs`
- `tests/unit/Spatial.Transformations.ProjNet.Tests/ProjWktProjectionMethodTests.cs`
  (ADR-0172's own control points, which this change does not move)

## Measurements

PROJ 9.8.1 through pyproj 3.8.0, `always_xy`, on the document's own ellipsoid
with no datum shift, 2026-10-01 (SpatialEngine-9r3; re-checked at re-land).

The ESRI WKT1 document for EPSG:3078 imports to `+proj=omerc … +alpha=-22.74444
+gamma=-22.74444`, and a document of the same dialect carrying an explicit
`Angle_From_Rectified_To_Skew_Grid` imports to the same proj string — PROJ
reads that parameter and discards it. That is what the engine refuses rather
than reproduces.

Forward and inverse control points, EPSG:3078 as `projinfo -o WKT1_ESRI` spells
it, largest deviation over the five points in the test class: 1e-6 m forward
and 1e-6 degrees inverse, the tolerance every method in the map is held to.

| Point (lon, lat) | PROJ easting | PROJ northing |
| --- | --- | --- |
| −86.0, 45.3091666666667 (projection centre) | 499840.25238587055 | 528600.3033698285 |
| −85.0, 45.5 | 577967.33091758355 | 550286.51057439297 |
| −83.0, 44.0 | 740335.1544894611 | 387567.5326477429 |
| −87.5, 46.0 | 383695.8067500545 | 606443.3155174358 |
| −84.5, 43.5 | 621114.09052158287 | 328757.81460122485 |

What the convention is worth: with `rectified_grid_angle` at zero — the grid a
reader that maps this spelling and reads no angle serves — the projection
centre is 2,046,891 m east and 411,693 m north of the row above.
