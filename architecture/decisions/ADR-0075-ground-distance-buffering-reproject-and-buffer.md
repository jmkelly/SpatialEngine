---
status: proposed
date: 2026-09-27
deciders: maintainer + agent
---

# ADR-0075: Ground-distance buffering is reproject-and-buffer, within a stated tolerance

## Context

The GeoServices `buffer` operation (§7.0.6) applies a `unit` to the
distances, buffers in a third CRS (`bufferSR`), and can buffer geodesically.
The engine only had `IGeometryOperations.Buffer`, which is planar: it
buffers in whatever units the coordinates are already in. So the adapter
rejected two requests ArcGIS serves — `geodesic=true` and `unionResults=true`
— and rejected a *linear* `unit` against a *geographic* buffer CRS, forcing
every caller that wanted a 1 km buffer around a WGS 84 feature to also
invent a projected `bufferSR` and hold it in sync. That is the single most
common way real clients call buffer, and it was a client burden created by
an engine gap.

The research for this bead offered two routes:

1. **A real geodesic buffer** — buffer along great circles. Correct at the
   poles and over the antimeridian, but genuinely hard there, and it wants
   its own test corpus before it can be trusted.
2. **Reproject-and-buffer** — project onto a suitable local projection,
   buffer, project back. Simple, and correct within a tolerance that depends
   on the extent, so the extent has to be part of the projection choice and
   the tolerance has to be stated rather than assumed.

The bead recommends (2) as the first step, because it composes with (1)
later: a true geodesic verb can replace the middle of the same pipeline
without changing anything at the edges.

## Decision

Take route 2 and land it as its own contract verb.

**`IGeodesicBuffering`** (new, `Spatial.Contracts`) — `Buffer(geometry,
distanceMetres, quadrantSegments, cancellationToken)`. One method: buffer by
a distance on the ground, whatever CRS the geometry carries, and return the
result in that same CRS. Negative distances erode. Core geometry types only
(ADR-0005); `Spatial.Contracts` still references only `Spatial.Core` and
takes no package.

**Implementation `ProjNetGeodesicBuffering`** (in
`Spatial.Transformations.ProjNet`, because the working plane is a projection
and ProjNet types must not leave that assembly). It is a composition, not a
new algorithm: pick a working plane, project, call
`IGeometryOperations.Buffer` on the plane, project back. It takes
`IGeometryOperations` and `IGeometryProcessing` by constructor; `Spatial.Host`
composes them in `EngineServices`.

**The working plane** is a transverse Mercator with unit scale
(`scale_factor = 1`) on the central meridian through the centre of the work,
latitude of origin at the centre, on the *source's own geodetic datum* so
the round trip is datum-consistent. A transverse Mercator is conformal, so
the scale is 1 in every direction on the central meridian: the plane is
locally isometric there, exact along that meridian, and the relative
distance error grows with the square of the offset from it.

**The stated tolerance: 0.05% relative (1 part in 2000), for a working
radius up to 300 km.** The working radius is the input part's envelope
half-diagonal plus the buffer distance — the distance the plane has to hold
faithfully. Measured against a Vincenty geodesic on WGS 84, the worst
boundary deviation of a geodesic circle is 0.02% of the radius at a 222 km
working radius (0.03% at 85°N) and 0.15% at 600 km, so 300 km sits inside
the stated 0.05% with margin. Past the limit the verb fails
`invalid.arguments` naming the radius, the limit and the remedy (a projected
CRS) rather than returning a shape it cannot stand behind. Both constants
are public on the implementation (`WorkingRadiusLimitMetres`,
`StatedRelativeTolerance`) so the tests and the docs quote the number the
code enforces.

**Per part, not per geometry.** Each part of a multi-part geometry gets its
own plane, so the tolerance follows the part's extent rather than the whole
collection's — a multipoint of stores 400 km apart buffers fine as two
1 km circles, where one plane for the collection would be refused. The parts
are dissolved with `IGeometryProcessing.Union` afterwards, which is what the
planar path does in a single pass and what keeps overlapping parts from
self-overlapping. The dissolve runs in the geographic CRS; longitude and
latitude are a valid topology space, and the only two places that bites —
the poles and the antimeridian — are exactly the two this verb refuses or
where the projection is locally exact.

**Refusals, each naming its reason:** a geometry with no CRS, a projected
CRS (that is `IGeometryOperations.Buffer`'s job), a centre within 89° of a
pole (the meridians converge, so distances along them are not comparable),
and a working radius past the limit. Over the antimeridian the answer wraps
its longitudes into `[-180, 180]`, which is the normal representation for
geographic data and is measured against the geodesic like anywhere else.

**The adapter** (`GeometryService.Buffer`) now routes per input geometry:

| Request | Path |
| --- | --- |
| linear `unit`, geographic buffer CRS | `IGeodesicBuffering`, distance in metres; `geodesic` accepted either way |
| everything else | `IGeometryOperations.Buffer` in the buffer CRS via `ICoordinateTransforms` (unchanged) |
| `unionResults=true` | buffer per input, then `IGeometryProcessing.Union`; one geometry in the result array |
| `unionResults=false` | one result per input (unchanged, and the Esri default) |

`geodesic=true` is **served** where a ground distance is what the request
already means (a linear `unit` against a geographic buffer CRS), and
**refused by name** where it is not: an angular `unit`, no `unit`, or a
projected `bufferSR`. In those cases the reason is stated and points at the
parameter to change — never answered planar.

## Consequences

- One new contract interface and one new implementation; no change to
  `IGeometryOperations` and no change to the planar path, which is still
  what a projected `bufferSR` asks for.
- `unionResults` is served, because `IGeometryProcessing.Union` already
  existed; the only new work was the routing and a guard that inputs
  resolving to different references are refused rather than dissolved across
  CRSs.
- The tolerance is a number the code enforces and the tests measure, not a
  promise in a comment. Both are named in one place.
- The accuracy is worse than a true geodesic buffer, and worse than a UTM
  zone, and callers who need either already have the `bufferSR` path — which
  is untouched and is still the exact answer inside a valid zone. The
  failure mode for large extents is a refusal, not drift.
- Swapping in a real geodesic buffer later means replacing the middle of
  this pipeline: the contract, the adapter routing and the tolerance's
  standing test do not have to move.
- No HTTP route was added for the verb. It is reached through the
  GeoServices `buffer` operation, which is the surface the bead is about;
  the clients are unchanged.
- Native AOT is untouched: the host stays JIT-compiled and no new package
  enters the repository.

## References

- ADR-0005 (implementation types stay in their implementation)
- ADR-0033 (in-process service interfaces)
- ADR-0035 (GeoServices boundary adapter)
- ADR-0036 (geometry measure/processing/relation verb faces)
- `architecture/references/geoservices-compatibility.md` §2 (buffer row)
- `architecture/distilled/contracts.md` (geometry verbs)
