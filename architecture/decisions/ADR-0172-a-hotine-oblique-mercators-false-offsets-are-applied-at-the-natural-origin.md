---
status: accepted
date: 2026-10-01
deciders: maintainer + agent
amends: ADR-0086
related: ADR-0027, ADR-0153, ADR-0170
summary: EPSG's reading wins: a Hotine Oblique Mercator (variant A, method 9812) definition's false easting and northing are applied at the **natural origin**, and the reader resolves variant A to ProjNet's `Oblique_Mercator`, which is the projection that does that — agreeing with PROJ 9.8.1 to 1e-6 m on four grids over two ellipsoids. `Hotine_Oblique_Mercator` is the variant B (Snyder Alternate B) reading, where the centre lands on the offsets, and is 2,047 km away; nothing is derived and no number is moved.
---

# ADR-0171: A Hotine variant A's false offsets are applied at the natural origin

## Context

The WKT method map resolves a projection method to a ProjNet projection only
where the two have been measured against PROJ and found to compute the same
thing (ADR-0027, ADR-0086). Hotine Oblique Mercator **variant A** (EPSG method
9812) was measured and left out: ProjNet's `Hotine_Oblique_Mercator` applies a
definition's false easting and northing at the **projection centre**, so the
centre lands on them exactly — Snyder's *Alternate B* convention — while EPSG
states them at the **natural origin**. On the Michigan grid that is one
translation, and it is 2,047 km: the centre is 2,046,891 m west and 4,882,610 m
north of where PROJ puts it, and so is every other point of the definition.
SpatialEngine-u2x.26 sized the gap and recorded this bead, because the repair
is a contract-level call rather than an adapter detail: **does the engine
follow EPSG's reading of a variant A definition, or keep ProjNet's and refuse
the definitions that disagree?** The other two divergences it recorded were
decided the same way (ADR-0153 for the polar stereographic variant B, ADR-0170
for Krovak's axes).

The decisive fact was not visible from the sizing bead's numbers: **ProjNet
has both readings.** `Hotine_Oblique_Mercator` and `Oblique_Mercator` are the
same oblique Mercator with one difference, and ProjNet keys that difference on
its own `AuthorityCode` — 9815 takes the offsets at the projection centre,
9812 at the natural origin. Its variant A class is the one named
`Oblique_Mercator`, and it is what PROJ's variant A computes. So this is not a
parameterisation to translate. It is a method map entry pointing at the wrong
of two classes.

## Decision

**The engine follows EPSG. Variant A resolves to `Oblique_Mercator`, keyed on
the normalised method name, and every parameter is read as the document
states it.**

1. **Variant A maps to the natural-origin projection.**
   `Hotine Oblique Mercator (variant A)` (EPSG 9812) resolves to ProjNet's
   `Oblique_Mercator`; `Hotine Oblique Mercator (variant B)` (EPSG 9815) keeps
   resolving to `Hotine_Oblique_Mercator`. The method name is the only thing
   that distinguishes them, and it is the only thing that needs to: PROJ's own
   `omerc` takes the same choice on the same key (`+no_uoff`).

2. **No parameter is derived, translated or moved.** This is where it differs
   from ADR-0153, which had to turn a latitude of standard parallel into a
   pole and a scale factor because ProjNet had no parameter for it. Here the
   parameter list is identical in both conventions and the difference is
   entirely in the class. A variant A definition's false offsets are handed on
   exactly as stated, whether the document spells them `Easting at projection
   centre` (the vendored EPSG:3078 WKT) or `False easting` (EPSG:29874,
   ESRI:102366) — both spellings are already read, and neither is adjusted.

3. **The offsets are never read as the centre's coordinates.** A reader may not
   "helpfully" place the projection centre on them for a variant A document,
   and no test or assertion inverts them. On the Michigan grid that is 2,047 km
   of wrong answer, and on the Sarawak grid it would put the two RSO grids of
   the same datum on each other.

4. **The map's class column is not the method name.** `Oblique_Mercator` is
   EPSG 9812 and `Hotine_Oblique_Mercator` is EPSG 9815, so nothing may infer
   one from the other by name; the row is the only statement, and the control
   points are what hold it.

5. **Nothing else moves.** The builder, the axes rule (ADR-0170) and the
   catalogue are unchanged: the reader hands over a class name and the
   document's parameters, exactly as for the other nine methods.

## Alternatives

- **Keep ProjNet's reading and refuse variant A definitions by name.** This is
  what the method out of the map does today, and it is the other half of the
  bead's question. Rejected: it serves nothing. The engine has the projection
  that computes EPSG's numbers, the refusal was sized as a gap rather than as a
  rule, and the cost of honouring it is paid by every caller who asks for a
  Michigan or Sarawak coordinate.
- **Translate the offsets in the reader** — subtract the projection centre's
  own un-offset coordinate from the stated offsets and hand ProjNet the
  difference, which is the same projection by construction. Rejected: it is
  arithmetic the engine does not need, it moves numbers EPSG publishes, and it
  would have to be re-derived per definition and per ellipsoid. The second
  clause of the decision — the parameters are handed on as stated — is the
  test of whether a future definition needs it.
- **Translate the offsets in `ProjEpsgCatalog.Build`.** Rejected on ADR-0153's
  grounds: the builder is the one programmatic path every definition shares,
  and a WKT-only convention would land there knowing which dialect a
  definition arrived in.
- **Apply the natural origin to variant B as well**, on the argument that
  Snyder's Alternate B is a local convention and EPSG is the registry everyone
  reads. Rejected: variant B's whole contract is that the centre lands on the
  offsets. The Swiss LV95 puts its projection centre at (2600000, 1200000), and
  that is what PROJ computes for it; a variant B definition read on variant A's
  terms would be the same 2,000 km error the other way round. Both readings are
  right, on their own definitions.
- **Keep variant A out of the map until a variant A grid is vendored.** Not
  decided here, but not a reason to refuse the method: the reader serves any
  document the moment one is vendored, and the control points do not need a
  catalogue row to exist.

## Not decided

- **The ESRI WKT1 spelling of variant A**, `Hotine_Oblique_Mercator_Azimuth_Natural_Origin`
  (what `projinfo` emits for EPSG:3078 in the WKT1 ESRI dialect). It is not in
  the map, because such a document states no angle from rectified to skew grid
  and the reader would hand ProjNet a default of zero and serve a skewed grid
  that is not rotated — a parameter accepted and ignored. Deciding the absent
  parameter (EPSG's convention is the azimuth of the initial line) is a separate
  record; filed as SpatialEngine-9r3.
- **Which codes the catalogue vendors.** The Michigan, Sarawak, Oregon and
  Alaska grids read the day a row exists (ADR-0086).
- **Which convention the engine calls which in its own prose.** The map is
  unambiguous and the ADR says so; nothing yet asks the catalogue to label a
  definition's variant for a caller.

## Consequences

- **The method map holds ten measured methods**, and one more of the three
  divergences SpatialEngine-u2x.26 recorded is repaired: only Krovak's axes
  remain a named failure, and that one is refused on its axes by ADR-0170.
- **The reader serves four more families of definition** — the Michigan and
  the other NAD83 oblique Mercators, the Sarawak RSO, the Oregon and Columbia
  River grids and the Alaska state plane band — none of which is vendored yet,
  so nothing served to a caller changes.
- **The cost is that two ProjNet classes now differ by one term, and only the
  method map and the control points say which is which.** A future method that
  reuses one of these class names has to come back here, because nothing in
  `Oblique_Mercator`'s own signature says which convention it implements.

## References

- ADR-0027 §2 (the method map, and the rule that a method is in it only where
  it is measured to agree), ADR-0086 (the catalogue is WKT read by a reader of
  our own), ADR-0153 (the sibling repair, and the case for reading in the
  reader), ADR-0170 (the sibling repair that was a refusal)
- SpatialEngine-u2x.26 (the measurement and the 2,047 km sizing),
  SpatialEngine-r4o (this record and its repair)
- `src/Spatial.Transformations.ProjNet/ProjWkt.cs` (the `Projections` map and
  its method table)
- `tests/unit/Spatial.Transformations.ProjNet.Tests/ProjWktProjectionMethodTests.cs`
  (`The_Hotine_variant_A_false_offsets_are_applied_at_the_natural_origin`,
  `A_method_verified_against_PROJ_transforms_to_the_PROJ_coordinate`,
  `A_method_verified_against_PROJ_resolves_to_its_ProjNet_projection`)

## Measurements

PROJ 9.8.1 through pyproj 3.8.0, `always_xy`, on each definition's own
ellipsoid with no datum shift, forward and inverse, 2026-10-01. The reference
is PROJ's own reading of the same WKT, so the variant is PROJ's choice too
(PROJ emits `+no_uoff` for method 9812, and none for 9815).

ProjNet's `Oblique_Mercator` (the variant A class) against PROJ, largest
deviation over 17 forward control points across the four definitions:

| Definition | Method | Ellipsoid | Points | Largest deviation |
| --- | --- | --- | --- | --- |
| EPSG:3078 Michigan | 9812 | GRS 1980 | 5 | 2.8e-9 m |
| EPSG:29874 Sarawak LSD | 9812 | Everest 1830 (1967) | 4 | 2.6e-8 m |
| ESRI:102544 Oregon Coast | 9812 | GRS 1980 | 4 | 3.7e-9 m |
| ESRI:102366 Alaska zone 1 | 9812 | GRS 1980 | 4 | 2.8e-9 m |

The inverse round trips PROJ's own coordinates back to the given point, and
all 17 are asserted to 1e-6 m forward and 1e-6 degrees inverse, the tolerance
every method in the map is held to.

What the convention is worth, on EPSG:3078's parameters at the projection
centre:

| Reading | Easting | Northing |
| --- | --- | --- |
| PROJ 9.8.1, variant A (offsets at the natural origin) | 499840.25318077067 | 528600.3025232237 |
| ProjNet `Oblique_Mercator`, variant A | 499840.25318077183 | 528600.3025232255 |
| `Hotine_Oblique_Mercator`, variant B (offsets at the centre) | 2546731.4967949 | −4354009.8168466 |

The variant B row is the definition's own false easting and northing exactly,
and 2,046,891 m east and 4,882,610 m south of PROJ's centre — the 2,047 km of
SpatialEngine-u2x.26, confirmed.

One number in the sizing bead needed correcting, and the correction is worth
recording because it is a trap rather than an arithmetic slip. The bead's PROJ
figure for the Michigan centre, (499840.25238587055, 528600.3033698238), is
right for the **ESRI WKT1** spelling of EPSG:3078, which states EPSG's
six-decimal offsets (2546731.496, −4354009.816) where the vendored WKT2 states
2546731.4967949 and −4354009.8168466. The two PROJ figures differ by exactly the
difference between those offsets — 0.795 mm east and 0.847 mm south — so the
two documents are 0.8 mm apart and neither is wrong. Pinning PROJ's numbers
means pinning the *document*, not the CRS.