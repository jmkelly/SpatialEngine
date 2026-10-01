---
status: superseded
date: 2026-08-31
deciders: maintainer + agent
summary: Transformation contracts (`ICrsDirectory`/`ICoordinateTransforms`)
superseded-by: ADR-0033
---

# ADR-0027: Coordinate transformation contracts and the ProjNet adapter

## Context

Phase 7 (plan §16, Epic G) ships the coordinate transformation provider that
ADR-0009 promised: "Coordinate transformation is a capability plugin
(`spatial.coordinate.transform@1`)". It must also describe CRSs — clients
need name, family, axis order and units to interpret or display coordinates —
and it must do so with the repo's established plugin shape: versioned
contracts with shared conformance fixtures (ADR-0007/0026), no third-party
types on public boundaries (ADR-0005), canonical binary interchange across
the worker boundary (ADR-0020), and the engine's Core.Geometry fan-in kept
below the code-metrics ceiling (plan §16: Ca < 8; today 6).

A transformation library had to be selected. ProjNet 2.1.0 (NuGet `ProjNET`,
maintained by the NetTopologySuite team, LGPL-2.1) is the natural sibling of
the NetTopologySuite already in the repo: pure managed code,
netstandard2.1, no native grids or bindings, an EPSG-oriented coordinate
system model, and — critically — math transforms that consume and produce
**(x, y) = (longitude, latitude) for geographic CRSs and (easting, northing)
for projected ones**, exactly the engine's x-first geometry convention. No
axis swapping is needed inside the adapter; the axis-order tests pin that
behaviour. Alternatives with embedded full-EPSG catalogs exist but bring
native binaries or restrictive licensing; a WKT1-based library is honest
about its accuracy limits (below).

## Decision

1. **Two versioned capability contracts ship in `Spatial.PluginSdk.Transformations`**:
   - `spatial.crs.describe@1` — input `crs.identity` (a CRS identity string
     such as `EPSG:4326`), output `crs.description` (a structured
     `CrsDescription`: authority, code, name, family, axes with
     names/orientations/units, datum, ellipsoid). Its conformance examples
     are embedded in the contract class — they carry only strings, no
     geometry types.
   - `spatial.coordinate.transform@1` — input `geometry.transform`
     (a geometry, an optional `source` CRS defaulting to the geometry's own
     CRS, a required `target` CRS), output `geometry` stamped with the
     target CRS. **Its conformance fixtures ship with the conformance suite,
     not embedded in the descriptor**: the examples carry geometry values,
     and a second geometry-referencing SDK type would push
     `Spatial.Core.Geometry`'s fan-in to the diagnosis threshold (Ca 8,
     architectural-rigidity). The fixtures are equally data-driven — the
     suite drives the same examples against every provider — and the
     descriptor's empty example list survives the worker host's manifest
     compatibility check by construction.
2. **The reference implementation is `Spatial.Transformations.ProjNet`
   (provider `projnet@1`)** on ProjNet 2.1.0, with a private adapter and a
   embedded EPSG catalogue: the common geographic and projected CRSs
   written out (WGS 84, ETRS89, NAD83, OSGB36, RGF93; Web Mercator,
   British National Grid, Lambert-93, the Conus Albers) plus projected
   **families** generated
   from one parameter template — the UTM grid over all sixty zones in both
   hemispheres (EPSG 32601-32660 and 32701-32760) and the ETRS89 and NAD83
   UTM bands over their own datums. Generating the families is what makes
   "the code the client actually has" servable: enumerating instances left
   every zone but the listed ones an `invalid.arguments` failure. The
   catalogue's **definitions are EPSG WKT** and are read by a reader of our
   own (`ProjWkt`), which produces a geodetic or projected definition that
   the catalogue's one programmatic builder constructs through ProjNet's
   factory. We do not use ProjNet 2.1's WKT reader: it cannot read WKT2 at all
   ("'PROJCRS' is not recognized"), and where it can read WKT1 it takes the
   projection class from the document, so the widely published
   `Mercator_1SP` spelling of EPSG:3857 (the Google/OSRM/GeoServer dialect,
   and EPSG:900913) becomes a plain Mercator and puts Web Mercator **33 km**
   too far south at Berlin's latitude. Our reader intercepts the
   Pseudo-Mercator case before construction — by projection name, CRS name or
   EPSG 3857/3785/900913/102100/102113 authority — and routes it to the same
   programmatic construction the catalogue has always used, which is pinned
   byte-for-byte by test. Everything else is read as data: the WKT text is
   the definition, and construction stays on the path that is already
   trusted. The reader resolves a projection method to a ProjNet projection
   only where the two have been **measured against PROJ and found to agree**
   (the table is on the reader, the control points in
   `ProjWktProjectionMethodTests`): resolving a method name to a ProjNet
   projection is a claim that the two compute the same thing, and it is a
   claim worth making only where it has been checked. Anything else is a
   named `invalid.arguments`-shaped failure rather than a silent
   approximation. Albers Equal Area, Lambert Azimuthal Equal Area, Polar
   Stereographic (variant A) and Hotine Oblique Mercator (variant B) are
   measured and resolved; Polar Stereographic (variant B), Hotine Oblique
   Mercator (variant A) and Krovak are measured, found to diverge and left
   out, with the size of the divergence pinned by test.
3. **Axis order**: the engine convention is x-first for every CRS — x is
   longitude for geographic, easting for projected, y the second axis.
   Describe reports the CRS's declared axes; the adapter needs no swaps.
4. **Errors** are `invalid.arguments` naming the value: missing, malformed or
   unknown CRS identities (the catalogue serves its documented CRSs and the
   generated UTM families, nothing else), a source argument conflicting with the geometry's own CRS, and
   transformed coordinates outside the target CRS's valid area (a non-finite
   result is an actionable error, never poisoned geometry). Anything else is
   a provider failure. Both capabilities are inline, cancellable, pure.
5. **CRS descriptions cross the worker boundary in a new `$crs` inline wire
   tag**, extending the Phase 5 explicit-tag codec (ADR-0025) the same way
   `$geometry` did (ADR-0020/0026): bare JSON objects are still rejected;
   malformed `$crs` payloads fail with field-level errors.
6. **Accuracy is stated honestly**: modern datums (WGS 84, ETRS89, NAD83,
   RGF93 — and the ETRS89/NAD83-derived projections) use zero datum shift
   and agree with PROJ to sub-millimetre at the pinned control points. The
   projection maths itself is measured separately, per method, against
   PROJ 9.8.1 on the definition's own ellipsoid with no datum shift: every
   method in the reader's map agrees to a micrometre or better, forward and
   inverse, at points inside each definition's area of use. A control point
   only counts where it is off the defaults — the Hotine variant B points
   span the Swiss grid (azimuth 90°, rectified angle 90°) and the Borneo grid
   (53.3158°, 53.1301°), because the Swiss parameters are the ones ProjNet
   would use if the reader dropped them, so the Swiss grid alone cannot tell
   a reader that reads the oblique parameters from one that ignores them.
   OSGB36 uses the classic Helmert approximation (446.448, -125.157,
   542.060, 0.15, 0.247, 0.842, -20.489) because the WKT1 library has no
   grid support (OSTN15); control-point tests assert within 0.1 m and
   document the ceiling. The catalogue subset follows the EPSG Geodetic
   Parameter Dataset terms of use (attribution in the code).

## Consequences

- CRSs are describable and transformable by any provider behind the same
  contracts; a full-EPSG provider can replace `projnet@1` without touching
  the runtime, SDKs or conformance fixtures.
- The transformation plugin adds exactly one `Spatial.Core.Geometry`
  referrer (the transform runner), holding the fan-in at Ca 7 — the planned
  budget for a geometry-processing plugin.
- ProjNet 2.1's quirks are pinned by tests: its projected CRSs expose the
  horizontal datum through their geographic coordinate system, its own WKT
  reader is not usable (no WKT2, and a `Mercator_1SP` reading of Web
  Mercator that is 33 km out), and its math is (x, y)-ordered. The 33 km is
  measured in `ProjNetWktCatalogTests`, not asserted in a comment, and the
  coordinates of all fifteen codes the catalogue served before the WKT path
  are pinned to the last bit.
- The definition carries a datum's *shift*; it does not carry the datum's
  accuracy or its area of use. Those are attributes of the registered
  coordinate operation, which WKT states only inside a `BOUNDCRS` node and
  which the catalogue therefore keeps in a separate curated table
  (`EpsgDatumOperations`). ADR-0086 decides the split; ADR-0087 is the
  transformation graph that joins the two.
- Adding a CRS is adding its WKT. The reader is deliberately narrow — both WKT
  dialects, and a method map that holds a projection only where it has been
  measured against PROJ and agrees (SpatialEngine-u2x.26 measured the
  candidates and widened the map to eight methods; the three that diverged are
  out, and the decisions their repair needed are separate beads —
  SpatialEngine-r4o for Hotine variant A's false-offset origin, SpatialEngine-ufn
  for Krovak's axes, SpatialEngine-g2m for Polar Stereographic variant B's
  latitude of standard parallel). ADR-0153 read the last of those: the reader
  derives the pole and the scale factor at that pole from the latitude of
  standard parallel, on the definition's own ellipsoid and by PROJ's own
  expression, so the map now holds nine methods and the two that still
  diverge are named failures. ADR-0172 read the Hotine variant A one: the
  false offsets are applied at the natural origin, as EPSG states them, and
  ProjNet already had that projection under the name `Oblique_Mercator`, so
  the map now holds ten methods and Krovak's axes are the only divergence
  left — and those are refused on their axes by ADR-0170 rather than on the
  arithmetic.
- The `$geometry` interchange carries transformed results unchanged; only the
  contract ids and the `$crs` description value are new on the wire.
