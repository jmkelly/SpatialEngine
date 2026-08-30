# ADR-0027: Coordinate transformation contracts and the ProjNet adapter

Status: Accepted

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
   curated embedded EPSG catalogue of twelve common CRSs (WGS 84, ETRS89,
   NAD83, OSGB36, RGF93; Web Mercator, five UTM zones, British National
   Grid, Lambert-93). The catalogue is **built programmatically** through
   ProjNet's factory, not parsed from EPSG WKT: ProjNet 2.1's WKT reader
   maps the "Popular Visualisation Pseudo-Mercator" projection class to a
   plain Mercator_1SP, which distorts Web Mercator northings by ~33 km.
3. **Axis order**: the engine convention is x-first for every CRS — x is
   longitude for geographic, easting for projected, y the second axis.
   Describe reports the CRS's declared axes; the adapter needs no swaps.
4. **Errors** are `invalid.arguments` naming the value: missing, malformed or
   unknown CRS identities (the catalogue serves only its documented EPSG
   subset), a source argument conflicting with the geometry's own CRS, and
   transformed coordinates outside the target CRS's valid area (a non-finite
   result is an actionable error, never poisoned geometry). Anything else is
   a provider failure. Both capabilities are inline, cancellable, pure.
5. **CRS descriptions cross the worker boundary in a new `$crs` inline wire
   tag**, extending the Phase 5 explicit-tag codec (ADR-0025) the same way
   `$geometry` did (ADR-0020/0026): bare JSON objects are still rejected;
   malformed `$crs` payloads fail with field-level errors.
6. **Accuracy is stated honestly**: modern datums (WGS 84, ETRS89, NAD83,
   RGF93 — and the ETRS89/NAD83-derived projections) use zero datum shift
   and agree with PROJ to sub-millimetre at the pinned control points.
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
  horizontal datum through their geographic coordinate system, its WKT
  reader mis-classifies Pseudo-Mercator, and its math is (x, y)-ordered.
- The `$geometry` interchange carries transformed results unchanged; only the
  contract ids and the `$crs` description value are new on the wire.