---
status: accepted
date: 2026-08-31
deciders: quality-loop metrics gate (ADR-0028/0029 fan-in budget)
consulted: core-boundary.md, geometry-model.md, AGENTS.md
---

# ADR-0032: Geometry contract faces and the geometry codec namespace

## Context

Phase 8 closed the code-metrics gate by giving `Spatial.Core.Features` its
contract faces and moving `FeatureBatchCodec` to
`Spatial.Core.Features.Codec` (ADR-0029), while holding `Spatial.Core.Geometry`
to the fan-in discipline (Ca < 8, plan §16; ADR-0028 budget). Phase 10 then
shipped the demo provider, whose data handlers legitimately construct and
filter core geometry values, pushing `Spatial.Core.Geometry` to Ca 9 and
re-triggering the `architectural-rigidity` diagnosis (abstractness 0.09,
Ca 9, D 0.91).

The fan-in budget is now structurally exhausted: every spatially capable
provider must name core geometry values, and the geometry contract faces
themselves expose the value primitives (`Envelope`, `CoordinateReference`,
`GeometryType`, `CoordinateLayout`, `Ordinate`) — so any split that moves the
faces or the codec out of the namespace re-adds them as afferent referrers
from outside. Three previously considered levers are non-starters in code:
reducing Ca below 8 (only two consumers could ever drop, and contracting
them away breaks the plugin adapters ADR-0001/0002 mandate), reducing D
(inverts the documented leaf dependency) and making the namespace
"concrete-data-only" (it owns structural machinery by ADR-0020). Keeping the
value model open requires the same resolution the repo already applied to
its other hubs (Capabilities Ca 94/A 0.35, Resources Ca 31/A 0.38,
Core.Features A 0.33): carry the abstraction the namespace is depended on
through — raising abstractness above the 0.3 diagnosis threshold.

## Decision

1. **The geometry value model gains its contract faces, in place**:
   `IPoint`, `ILineString`, `IPolygon`, `IMultiPoint`, `IMultiLineString`,
   `IMultiPolygon` and `IGeometryParts` (one per concrete shape) plus
   `IGeometryFactory` (the static creation contract implemented by
   `GeometryFactory`), all in `Spatial.Core.Geometry` alongside the existing
   `IGeometry`/`ICoordinateSequence`. Concrete values stay the immutable
   implementations (ADR-0004); the faces expose only the structure adapters
   and codecs already read (`Coordinate`, `Sequence`, rings, parts, ordinate
   accessors). Adapter-side code binds the faces where it only inspects or
   transports the model: `PostgisEwkb`'s body writers and the NTS operation
   runner's ring validation now type against them. The instance surface of
   each class is unchanged; `GeometryFactory` remains all-static and its
   call sites are untouched (the class implements the interface, which is
   why it is `sealed` rather than `static` — C# does not let a static class
   implement an interface).
2. **`GeometryCodec` — and its thrown `CanonicalFormatException`, nested
   `Reader`/`Writer`/`NodeInfo` — moves to `Spatial.Core.Geometry.Codec`**,
   the same leaf-namespace shape ADR-0029 gave `FeatureBatchCodec`. Encoding
   is a distinct structural concern; the class name, format version and
   bytes are unchanged (callers add one `using`).
3. The metric gate result is read as: `Spatial.Core.Geometry` is a hub
   namespace carried by its faces (abstractness 0.31), the codec namespace
   is a leaf (Ca 2). The `architectural-rigidity` diagnosis continues to
   guide fan-in awareness, but abstractness — not a referrer head-count — is
   the binding constraint for core value hubs.

## Consequences

- The metrics gate is green with honest structure: 0 findings. `Core.Geometry`
  abstractness 0.31 ≥ 0.3 (the repo's hub-namespace norm), the codec
  namespace a leaf, no moved type re-entering the value namespace's afferent
  set.
- Public surface changed deliberately but additively: eight new interfaces
  (shape faces + factory contract) and the codec's namespace. Tests, SDK
  contracts, `Spatial.Core/AGENTS.md`, `architecture/geometry-model.md`,
  `architecture/core-boundary.md` and this ADR record the change together
  (plan §20/§22).
- Adapters and codecs that only inspect/transport shape data can now bind
  the shape faces instead of the concrete classes; providers that need an
  injectable builder bind `IGeometryFactory` (implemented by
  `GeometryFactory` today).