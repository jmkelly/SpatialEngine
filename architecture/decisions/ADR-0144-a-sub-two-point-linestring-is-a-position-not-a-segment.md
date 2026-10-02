---
status: accepted
date: 2026-10-01
deciders: maintainer + agent
summary: A **sub-2-point LineString is a position, not a segment**, so it is a valid value the render path does not reject (SpatialEngine-a74.2): the zero-coordinate one is the *empty* LineString and the one-coordinate one is neither empty nor a segment, and core states no minimum-coordinate rule because validation is a plugin verb (`IGeometryOperations.Validate`). A `line` layer strokes nothing for either (a lone `MoveTo` has no segment to cap), a `symbol` layer still labels the position the one-point one carries, the cull keeps a position in view, and the place stage **never hands one to the planar clip** — the clip bounds extent and a single position has none, so clipping it failed a whole render with an opaque `invalid.arguments` from an intersection nobody asked for. Rejecting the class at ingest stays open as its own decision (amends nothing; follows 0001, 0044, 0080).
---

# ADR-0144: A sub-2-point LineString is a position, not a segment

## Context

`GeometryFactory.CreateLineString` accepts any sequence, and `Spatial.Core`
states no minimum-coordinate rule: core is structural (ADR-0001), and
validation is a plugin verb (`IGeometryOperations.Validate`). `LineString.IsEmpty`
is `CoordinateCount == 0`, so the two short states are distinct:

- **zero coordinates** — the *empty* LineString: no position, no envelope, not
  drawn by anything;
- **one coordinate** — a *degenerate* LineString: one position with a
  zero-width, zero-height envelope, neither empty nor a segment.

Nothing stops a client producing either. The decoders and ingest read them
without complaint (a GeoJSON `LineString` needs only a position per element,
not two — `GeoJsonGeometryCodec` requires two *ordinates* per position, not two
positions per line), so a stored feature can carry one.

Two consumers then disagree about what that is. SpatialEngine-a74 measured the
raster half: `SkiaPathBuilder.AddRing` emits a lone `MoveTo` with no segment to
cap, so a 1-point LineString on a `line` layer strokes nothing under every
`line-cap` — indistinguishable, in a render, from a dropped feature. But on a
`symbol` layer the same value is a legitimate one-point label: ADR-0080's
candidate generator already offers a point geometry's position, and the same
generators already cope with degenerate geometry (the digest records "the
candidate generators … degenerate geometry" under ADR-0080's test surface).

Worse, the render was not merely quiet — it failed. `GeometryPipeline.Place`
clips source geometry to the dataset's source-CRS image before reprojecting (so
a dataset reaching the poles stays inside Web Mercator's domain). NTS's
`LineString` requires 0 or ≥ 2 coordinates, so the intersection behind
`IGeometryOperations` threw — and it threw for the **whole** geometry, so one
degenerate feature anywhere in a multi-line killed the entire render request
with an opaque `invalid.arguments` naming an intersection the caller never made.

So the bead had to settle an explicit question: is this class **rejected** at
ingest/normalisation with a structured `SpatialException(invalid.arguments)`, or
**documented as valid** with its render behaviour asserted?

## Decision

**A sub-2-point LineString is a valid value that is a position rather than a
segment. It is not rejected anywhere, and each consumer's treatment of it is
pinned rather than left to fall out of a loop.**

Rejecting it was considered and declined: it is a change to core validation
semantics, which is a core-project decision rather than a rendering fix (the
parent bead says so), and it would break a use that is legitimately rendering —
a symbol layer labelling a one-point feature — while every other path
(codecs, WKB/EWKB readers, GeoJSON ingest, the stores) already carries the
value without complaint. Rejecting it late, at ingest, is also the one option
that makes an already-stored dataset unrenderable rather than merely blank.

Concretely:

1. **A `line` layer strokes nothing for it, under every cap.** A lone `MoveTo`
   is not a segment. This is the picture that is right: a single point is not a
   stroke. It stays indistinguishable from a dropped feature in a render, which
   is why it is pinned by a test rather than left as an accident.

2. **A `symbol` layer still labels the one position it carries.**
   `symbol-placement: point` yields the position's candidate; the empty
   LineString yields none, and `symbol-placement: line` yields none for either,
   because there is no direction to run a label along.

3. **The viewport cull keeps a degenerate line in view.** `SimplifyAndCull`
   already simplifies only above two coordinates and culls on envelope
   intersection, so a one-point envelope inside the viewport survives. A
   position is either in view or it is not; it is not "too detailed" to draw.

4. **The place stage never hands one to the planar clip.** `ClipToBounds`
   returns a geometry holding a sub-2-point LineString unchanged, at any depth
   (a bare line, a multi-line member, or a member of any compound). The reason
   is not leniency: the clip bounds *extent*, and a single position has none, so
   there is nothing for the clip to bound — while the planar algorithm cannot
   represent the value at all. A compound that also holds a degenerate line is
   therefore left unbounded, which is the cheaper of the two ways of losing a
   clip this pipeline did not need in every other case.

This does not authorise a minimum-coordinate rule in core. A caller that wants
one asks `IGeometryOperations.Validate`, which is where validation lives; this
ADR fixes what a render *draws*, not what the model *permits*.

## Consequences

- A dataset holding degenerate geometry renders — on a line layer, blank at
  that feature; on a symbol layer, labelled — instead of failing the request.
  The failure it replaces was an `invalid.arguments` from an intersection the
  caller never asked for, so nothing was lost but the image.
- "Draws nothing" is no longer an accident of `AddRing`. It is a pinned
  behaviour with a named reason, so a future change to the path builder that
  started dotting these would be a red test rather than a surprise.
- The clip is skipped for a whole compound rather than per-member, so a
  multi-line with one degenerate member can carry geometry past the source-CRS
  image. That is the same trade PolarRenderTests already makes for the poles,
  and it is bounded by the same downstream projection: a degenerate line has one
  position, so there is nothing for it to push out of range that a transform
  would misplace.
- Rejecting this class at ingest remains open, as a separate decision with its
  own ADR, if a consumer ever needs it — this one does not authorise it.

## References

- ADR-0001 (geometry values are core; algorithms are not — hence no validation
  rule in core and a clip that delegates)
- ADR-0044 (the render pipeline whose place stage clips before reprojecting)
- ADR-0080 (label placement — the candidate generators that admit degenerate
  geometry)
- SpatialEngine-a74 (the stroke half: degenerate line geometry on the raster
  path)
- `tests/unit/Spatial.Rendering.Skia.Tests/SubTwoPointLineRenderTests.cs`
- `tests/unit/Spatial.Rendering.Skia.Tests/LineOrientationRenderTests.cs`
