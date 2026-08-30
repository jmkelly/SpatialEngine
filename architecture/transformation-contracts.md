# Coordinate Transformation Contracts

Read when implementing, replacing or conformance-testing a coordinate
transformation provider. Part of Phase 7 (plan §16, Epic G); implements
ADR-0005/0007/0009/0020/0025/0027.

## The two contracts

The CRS description and coordinate transformation capabilities ship as
versioned capability contracts in `Spatial.PluginSdk.Transformations` —
replaceable, provider-agnostic declarations (ADR-0002/0007):

| Contract | Id | Input | Output | Behaviour |
| --- | --- | --- | --- | --- |
| Describe | `spatial.crs.describe@1` | `crs.identity` — `crs` (identity string, e.g. `EPSG:4326`) | `crs.description` — a structured `CrsDescription` | Name, family, axes (name/orientation/unit), datum and ellipsoid of one CRS from the provider's catalogue |
| Transform | `spatial.coordinate.transform@1` | `geometry.transform` — `geometry`, optional `source`, required `target` | `geometry` | Transform the geometry from the source to the target CRS; the result carries the target CRS identity |

Both register one error variant, `invalid.arguments`, covering
missing/malformed/unknown CRS identities (the catalogue serves only its
documented EPSG subset), a `source` argument conflicting with the geometry's
own CRS, and transformed coordinates outside the target CRS's valid area. A
`source` omitted toggles to the geometry's own CRS identity — which then
becomes required (an un-CRS'd geometry cannot be transformed). Both
capabilities are inline, cancellable, pure. Argument names are stable and
shared (`TransformationArguments`).

## Axis order and the engine convention

The engine's geometry convention is **x-first for every CRS**: x is the
first axis — longitude for geographic, easting for projected — and y the
second (latitude / northing). `spatial.crs.describe@1` reports each CRS's
declared axes so clients know the interpretation. ProjNet 2.1's math
transforms consume and produce exactly this order, so the reference adapter
performs no axis swaps; the axis-order tests pin that with control points
(swapped input lands kilometres away, and Web Mercator's x depends only on
longitude).

## Interchange

Geometry arguments and results are core `IGeometry` values; across the
worker boundary they travel as canonical binary interchange in the
`$geometry` wire tag (SGEOM encoding, ADR-0020) — never as JSON geometry.
CRS descriptions travel in the new `$crs` wire tag (ADR-0027), a structured
object with the same explicit-tag rule as `$geometry`/`$bytes`: bare JSON
objects are rejected, and malformed `$crs` payloads fail with field-level
errors.

## Adapter semantics (`Spatial.Transformations.ProjNet`)

The reference implementation is the ProjNet plugin (provider `projnet@1`).
Its private pieces (ADR-0005) are the **curated EPSG catalogue**
(`ProjEpsgCatalog` — twelve geographic and projected CRSs built
programmatically through ProjNet's factory, with datum-to-WGS84 shifts; the
EPSG subset is attributed in source), the **description mapper**
(`ProjNetCrsMapper`) and the **transform runner** (`ProjNetTransformRunner`):

- **The engine's x-first convention needs no swaps**; the adapter transforms
  XY ordinates and keeps Z and M untouched. Result parts and rings carry no
  CRS (matching the codec convention); the top-level result is stamped with
  the target CRS identity. Empty geometries keep their type and layout.
- **Out-of-area coordinates are an actionable error**: a non-finite
  transformed coordinate (for example latitude 95 into Web Mercator) fails
  with `invalid.arguments` naming the point, never returning poisoned
  geometry.
- **Failure mapping**: identity/catalogue problems, ProjNet's unsupported
  transformation path and out-of-area coordinates are `invalid.arguments`
  naming the value; anything else is a provider failure. Cancellation is
  honoured before the synchronous algorithm runs; ProjNet itself has no
  cancellation hooks.
- **Accuracy is documented**: modern datums use zero shift and agree with
  PROJ to sub-millimetre at the control points; OSGB36 uses the classic
  Helmert approximation (no grid support in the WKT1 library) and is tested
  within 0.1 m of the official OSTN15 value.

## Conformance

Every provider of a standard capability must pass the same fixtures (plan
§18). The shared suite (`tests/conformance/Spatial.Conformance.Tests`,
`TransformConformance`) runs the describe contract's own embedded examples
(string-only) and the transform fixtures from `TransformConformanceExamples`
(geometry-carrying — shipped with the suite per ADR-0027, so the descriptor
declares none) — success, empty input, unsupported input, cancellation and
diagnostics — and asserts the invocation provenance (capability, provider,
resolution step, duration) on every outcome. The same invoker delegate drives
the in-process provider and the isolated worker package, so both must agree
on every result shape.