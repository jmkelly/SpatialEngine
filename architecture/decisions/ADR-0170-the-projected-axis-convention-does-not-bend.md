---
status: accepted
date: 2026-10-01
deciders: maintainer + agent
amends: ADR-0086
related: ADR-0027
summary: The engine's projected axis convention does not bend: a WKT definition whose own coordinate system declares axes other than easting then northing is **refused by name, on any method**, so EPSG:5513, EPSG:2065 and the Slovak Krovak variants stay out of the catalogue rather than being served as coordinates under a description that contradicts them; Krovak's arithmetic was never the problem, and the refusal now rests on the axes rather than on the method map.
---

# ADR-0170: The projected axis convention does not bend

## Context

The WKT method map resolves a projection method to a ProjNet projection only
where the two have been measured against PROJ 9.8.1 and agree (ADR-0027,
ADR-0086). Krovak (EPSG method 9819) was measured and left out, sized by
`The_Krovak_divergence_is_the_axis_convention_not_the_arithmetic`: at 14.42°E,
50.09°N PROJ gives (1042796.9662, 742949.4310) and ProjNet returns the same
two magnitudes negated and transposed. The arithmetic is right to the
millimetre. The axes are the difference, and the record that left Krovak out
named the question as undecided and tracked it here: **does the engine's axis
convention bend for a CRS that declares otherwise, or does Krovak stay out as
a named failure?**

The question is not academic because the engine builds every projected
definition the same way. `ProjEpsgCatalog.Build` passes `EastingAxis` and
`NorthingAxis` to `Factory.CreateProjectedCoordinateSystem` whatever the
document declared, and `ProjNetCrsMapper.Describe` reports the axes of the
system it was handed. So the axes a caller is told about are always easting
and northing — and the reader had no check that the document agreed. Put
Krovak in the method map as it stands and the catalogue would describe
EPSG:5513 as easting/northing and serve it numbers that are the negatives of
its westing and southing: not an approximation, a different coordinate system
under someone else's name, which is the silent-wrong-coordinates failure the
method map exists to prevent.

## Decision

**The projected axis convention does not bend, and `ProjWkt` refuses a
definition whose declared axes are not easting then northing.**

1. **The check is on the definition's own axes, not on the method.** After
   the base geodetic CRS and the method are read, and before the parameters
   are, the reader reads the PROJCS's own `AXIS` nodes — in `ORDER` order
   where the document states one, in listing order otherwise — and refuses
   the definition unless the first is easting and the second is northing.
   Both dialects are read: WKT2 quotes the direction (`AXIS["(X)",south]`),
   WKT1 leaves it a bare keyword (`AXIS["X",EAST]`), and the short forms
   `E`/`N` are accepted. Axis order is checked before parameters because a
   document the engine will not serve cannot be rescued by reading its
   numbers.

2. **A refusal names what it refused**: the axes as the document spells them
   (`'S-JTSK / Krovak' declares its axes as (X) south, (Y) west, which are
   not easting and northing…`). A transposed pair — northing first, easting
   second — is refused the same way, because the answer is to refuse rather
   than to swap.

3. **A document that declares no axes reads as it stands.** The WKT1
   dialect's shorthand is easting and northing, which is what the engine
   serves, and the vendored catalogue is full of such documents.

4. **A document that declares fewer than two axes, or one whose direction
   keyword is not one of the four, is refused by name** rather than assumed.
   A parameter is honoured or refused by name (AGENTS.md), and an axis is a
   parameter.

5. **The geodetic base's declared order is not examined.** Every EPSG
   document declares latitude first, and x-is-longitude for every geographic
   CRS is an older decision (ADR-0027 §3) that the axis-order tests pin.
   Re-deciding it here would refuse the entire catalogue.

6. **Krovak stays out, twice over.** Its method is not in the map (the
   existing named-failure test), and its axes would refuse the document even
   if the method were mapped — which
   `The_Krovak_axes_refuse_the_definition_whatever_method_it_names` pins by
   substituting a mapped method into the EPSG:5513 document. So the
   remaining gap is not "add Krovak": a future record that wants Krovak
   served has to answer the axis question this record just closed, which
   means bending the convention or publishing something other than
   EPSG:5513's coordinates under EPSG:5513's name.

## Alternatives

- **Serve EPSG:5513 in its declared axes — x is southing, y is westing.**
  Rejected: it makes x not-an-easting, and x-is-the-first-declared-axis is
  assumed by tiling, envelopes, storage and the Esri REST surface. The cost
  is an axis-order model on every coordinate in the engine, which is a
  contract change, not a reader change.
- **Serve the engine's own x-first reading of Krovak — (−westing,
  −southing) — under the EPSG:5513 identity.** Rejected: it is what the
  reader would have done, and it is the wrong answer twice over. The numbers
  are not the ones EPSG publishes for EPSG:5513, so no other consumer of that
  code agrees with them, and the description the engine reports for them says
  easting and northing, which is false. A CRS served under someone else's EPSG
  code with numbers that code does not publish is worse than a refusal.
- **Map the declared axes onto the engine's** — read `(south, west)` as the
  engine's `(x, y)` and negate. Rejected: that is the previous option with a
  name attached. The axes are a property of the CRS, not a presentation, and
  the negation only makes the numbers invertible, not correct.
- **Add a documented per-CRS axis order to `CrsDescription` and let every
  consumer swap.** Not rejected on merit — it is the only route to serving
  these CRSs honestly, and it is a contract change with a migration
  (Describe, transforms, tiles, storage, rendering). Not decided here: no
  bead asks for it, and this record's job is to stop the wrong thing being
  served in the meantime.
- **Keep the refusal as it was, an omission from the method map.** Rejected:
  it was one entry away from a silent wrong-coordinates serving, and nothing
  tested that. The rule makes the refusal a property of the reader.

## Not decided

- **Whether `Describe` should report a definition's *declared* axes.** It
  reports the axes of the system it was handed, which are always the engine's
  x-first ones; for EPSG:4326 that is lon/lat where the document declares
  lat/lon. Reporting the declared order honestly would mean reporting an
  order the transform does not use, which needs its own decision.
- **Which codes the catalogue vendors.** Nothing above decides that EPSG:5513
  is ever vendored; the reader serves the day a row exists.
- **The Slovak Krovak variants** (EPSG:2065, 5514 and their
  east-oriented siblings) are decided by the same rule as EPSG:5513 and need
  no separate record.

## Consequences

- **The axes are refused before the method is resolved**, so the failure a
  caller sees for a south/west document names the axes — the thing that has
  to change — rather than whichever method the document also names.
- **Serving a Krovak CRS is now a contract question, not a reader change**,
  and it is recorded as one. That is the cost: the engine serves no CRS whose
  axes it does not use, which for the Krovak family is every member.
- **The rule is narrow in practice**: every vendored definition (13 projected,
  6 geographic) declares `(E) east, (N) north` or no axes at all, so nothing
  already served changes.
- **A future axis-support record has to move this one**, because the check
  that implements it lives where the convention is decided.

## References

- ADR-0027 §3 (the x-first convention; the method map; Krovak's named
  failure)
- ADR-0086 (the catalogue is WKT read by a reader of our own; the reader is
  where a definition's meaning is settled)
- SpatialEngine-u2x.26 (the measurement that sized the gap), SpatialEngine-ufn
- `src/Spatial.Transformations.ProjNet/ProjWkt.cs`
  (`CartesianAxesAreEastingThenNorthing`, `DeclaredCartesianAxes`)
- `tests/unit/Spatial.Transformations.ProjNet.Tests/ProjWktProjectionMethodTests.cs`
  (`A_projected_definition_declaring_axes_the_engine_does_not_serve_is_refused_by_name`,
  `The_Krovak_axes_refuse_the_definition_whatever_method_it_names`,
  `The_Krovak_divergence_is_the_axis_convention_not_the_arithmetic`)

## Measurements

SpatialEngine-u2x.26's PROJ 9.8.1 measurement (pyproj 3.8.0, `always_xy`, on
S-JTSK's own Bessel 1841 ellipsoid, no datum shift, 2026-10-01), restated
here because the numbers are the argument and this record did not re-run them
(pyproj is not installed on this host):

| Point (lon, lat) | PROJ 9.8.1, EPSG:5513 in its own axis order | ProjNet, built easting/northing |
| --- | --- | --- |
| 14.42°E, 50.09°N | (1042796.9662, 742949.4310) — southing, westing | (−742949.431, −1042796.9662) |

The magnitudes agree to the millimetre; the signs, the order and the axes do
not. The error the engine would serve under EPSG:5513 is therefore not a
distance to bound — every coordinate is wrong, by the whole of the grid — and
the only safe answers are the refusal this record decides or an honest axis
model, which it does not.

Before the check, `ProjWkt.TryParse` accepted a document declaring
`(X) south, (Y) west` on a method already in the map (measured on the LAEA
Europe definition, whose projection is unaffected by the axes) and built it
exactly as the easting/northing document beside it — a CRS described as
easting and northing whose numbers the document says are southing and
westings. That is the failure this record closes, and the new tests fail
without it.