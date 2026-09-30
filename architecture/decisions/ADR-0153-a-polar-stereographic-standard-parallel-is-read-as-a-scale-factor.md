---
status: accepted
date: 2026-10-01
deciders: maintainer + agent
amends: ADR-0086
related: ADR-0027
summary: EPSG's Polar Stereographic (variant B) is read as the variant A projection ProjNet has, with the pole and the scale factor at that pole **derived in the reader** from the definition's latitude of standard parallel and its own ellipsoid, by PROJ's own expression (0.9727690128917972 for EPSG:3031); the standard parallel is turned into those two parameters rather than handed on as one the projection does not read, and a document that states it alongside a scale factor is refused by name.
---

# ADR-0153: A polar stereographic's standard parallel is read as a scale factor

## Context

The catalogue's reader resolves a WKT projection method to a ProjNet
projection, and that resolution is a claim that the two compute the same thing
— a claim only made for a method measured against PROJ 9.8.1 and found to
agree (ADR-0027, ADR-0086). ProjNet's `Polar_Stereographic` is the EPSG
**variant A** formulation: a pole, a scale factor at that pole, and the false
offsets. It agrees with PROJ there to well under a micrometre, at both poles
and through 2,000 km of offsets, so variant A is in the method map.

EPSG **variant B** (method 9829) is the same projection stated a different
way: a latitude of standard parallel, and no scale factor. ProjNet has no
parameter for that, and the definition has no pole — variant B states neither.
Handed the definition as EPSG writes it, ProjNet lands 527 km of northing away
from where PROJ puts EPSG:3031 at 0°E, 80°S, so the method was left out of the
map and named as a failure (SpatialEngine-u2x.26, which sized the gap and
recorded the repair as SpatialEngine-g2m).

That left two questions the sizing bead was not authorised to answer: **where
the derivation lives** — the reader that reads the method, or
`ProjEpsgCatalog.Build` that builds the coordinate system — and **which
expression** it is.

## Decision

**The reader derives the two parameters variant B omits, in place of the
standard parallel, from the definition's own ellipsoid and by PROJ's own
expression. The builder is unchanged.**

1. **Variant B resolves to the same ProjNet projection as variant A**, keyed on
   the normalised method name, so `Polar Stereographic (variant B)` and the ESRI
   `Polar_Stereographic_Variant_B` both reach `Polar_Stereographic`.

2. **The pole is the sign of the standard parallel** (`latitude_of_origin` of
   −90° or +90°), and **the scale factor is derived from the standard parallel
   and the definition's ellipsoid** — PROJ's expression for
   `+proj=stere +lat_ts=` (`src/projections/stere.cpp` and `src/tsfn.cpp`):
   `cos φ · sqrt((1+e)^(1+e) (1−e)^(1−e)) / (2 · t(φ) · sqrt(1 − e²sin²φ))`,
   where `t` is the isometric-latitude term, evaluated at the parallel's
   **absolute** latitude, because PROJ takes the hemisphere from the pole and
   the reader takes it from the sign of the same number. On WGS 84 that is
   0.9727690128917972 for EPSG:3031's −71° and 0.9698581903263522 for
   EPSG:3413's +70°.

3. **The standard parallel is not handed on.** It is a parameter ProjNet does
   not read, so the reader consumes it. A non-variant-B method that states one
   is refused by name rather than passed a parameter that would be ignored.

4. **Every other parameter is read as it stands** — the longitude of origin and
   the false offsets of EPSG:3032's 6,000 km included — and the two derived
   parameters take the standard parallel's place in the document's order.

5. **A parameter is honoured or refused by name.** A variant B definition with
   no latitude of standard parallel, one whose parallel is 0° (which names no
   pole), and one that states a scale factor or a natural origin *as well as* a
   standard parallel are each a named failure, not a projection built from half
   a definition.

## Alternatives

- **Derive in `ProjEpsgCatalog.Build`.** Rejected: the builder is the one
  programmatic path every definition shares — a hand-written row, a generated
  family member and a WKT definition alike (ADR-0086) — so a WKT-only
  method's parameterisation would land in the one place that must not know
  which dialect a definition arrived in, and the derivation needs the base
  ellipsoid, which a row would have to restate. The reader already has the
  precedent: it supplies Pseudo-Mercator's four parameters when the document
  omits them.
- **Use EPSG Guidance Note 7-2's `k0 = m(φc)/m(90°)` for the ellipsoidal
  case**, the form the sizing bead named. Rejected on measurement: with
  `m(φ) = cos φ / sqrt(1 − e²sin²φ)`, `m(90°)` is zero, so the quoted form
  cannot be the expression PROJ evaluates. PROJ's is the isometric-latitude one
  above, and the difference is not academic — handed to ProjNet, the
  isometric-latitude reading of the same parameter lands 6.1 km from PROJ's
  EPSG:3031, and only PROJ's own expression is within a micrometre.
- **Keep the method out and refuse it by name, as before.** Rejected: the gap
  was never ProjNet's arithmetic. It is one parameter the projection states
  differently, and the reader already exists to say what a document means in
  the terms the projection takes.
- **Vendor rows for EPSG:3031, 3032, 3413, 3976, 3995 and 3996.** Not decided
  here: which codes the curated catalogue carries is ADR-0086's question, and
  nothing above needs it. The reader serves any of those documents the moment
  one is vendored.

## Not decided

- **Which codes the catalogue vendors.** The six variant B polar grids read
  correctly the moment a row exists; adding them is a catalogue decision with
  its own reasons (area of use, the datum's operation data).
- **A variant B definition on a sphere.** The reader already refuses a
  spherical ellipsoid outright (`ProjWkt.TryFlattening`), so the expression's
  `e = 0` branch is unreachable through the catalogue and is left unwritten
  rather than pinned by a test that cannot be built.

## Consequences

- **The method map widens to nine measured methods**, and two of the three
  divergences SpatialEngine-u2x.26 recorded are now repaired: the polar
  stereographic variant B (527 km) here, Hotine variant A and Krovak still
  out as named failures.
- **A definition's parameters now depend on its base ellipsoid**, not on the
  method alone: the same standard parallel on two ellipsoids derives two scale
  factors. That is the point — it is what makes the derived parameter agree
  with PROJ rather than merely look plausible.
- **The reader is the only place that knows this.** `ProjEpsgCatalog.Build`
  still takes a name-and-value list, so a caller that hand-builds a variant B
  projection from a scale factor of its own inventing gets whatever it built.
  That is the same bargain the method map already makes for every method.
- **The cost is a formula in the reader.** Six lines of transcendental
  arithmetic that exist to translate one EPSG parameterisation into another,
  pinned against PROJ's control points rather than against its own algebra.

## References

- ADR-0027 (the method map, and that every method in it is measured against
  PROJ 9.8.1)
- ADR-0086 (the catalogue is WKT read by a reader of our own; the reader is
  where a method becomes parameters)
- SpatialEngine-u2x.26 (the measurement, the 527 km sizing, and the record of
  this gap)
- PROJ 9.8.1 `src/projections/stere.cpp` (`stere_setup`, the polar `akm1`) and
  `src/tsfn.cpp` (`pj_tsfn`)
- `src/Spatial.Transformations.ProjNet/ProjWkt.cs`
  (`PolarStereographicVariantB`, `PolarScaleFactor`)
- `tests/unit/Spatial.Transformations.ProjNet.Tests/ProjWktProjectionMethodTests.cs`

## Measurements

PROJ 9.8.1 through pyproj 3.8.0, `always_xy`, on each definition's own
ellipsoid with no datum shift, 2026-10-01. The scale factors are PROJ's own
values, recovered by handing ProjNet the variant A parameters and solving the
multiplier against `+proj=stere +lat_ts=`.

| Definition | Standard parallel | Derived scale factor | PROJ scale factor | Control point (lon, lat) | PROJ (E, N) | Engine agreement |
| --- | --- | --- | --- | --- | --- | --- |
| EPSG:3031 | −71° | 0.9727690128917972 | 0.9727690128917974 | 0°, −80° | 0, 1089179.4556261837 | 2e-9 m |
| EPSG:3031 | −71° | | | −45°, −72° | −1393947.5396750527, 1393947.5396750532 | 7e-10 m |
| EPSG:3032 | −71° | | | 120°, −75° | 7255380.793258387, 7053389.560610154 | 9e-10 m |
| EPSG:3413 | +70° | 0.9698581903263522 | 0.9698581903263526 | −45°, 80° | 0, −1085920.2973930992 | 2e-9 m |

Twelve forward and twelve inverse control points across the three definitions
are asserted to 1e-6 m (a micrometre), the tolerance every method in the map
is held to; the largest deviation among the new points is 2e-9 m. The error
the derivation removes, at EPSG:3031's 0°E, 80°S with a scale factor of 1 at
the pole instead of the derived one, is 527,466 m of northing; the
isometric-latitude reading of the same parameter is 6,134 m away from PROJ.
