---
status: proposed
date: 2026-09-13
deciders: maintainer + agent
---

# ADR-0036: Geometry measurement, processing and relation verbs are separate SDK interfaces

## Context

ADR-0035 requires the GeoServices adapter to map protocol verbs onto engine
verbs and to keep spatial algorithms out of the adapter. The GeoServices
Geometry Service (spec §7) needs more than `IGeometryOperations`' four verbs
(`buffer`, `intersection`, `validate`, `simplify`): `areasAndLengths`,
`lengths`, `distance`, `labelPoints`, `convexHull`, `difference`, `union`,
`densify`, `relation` and `simplify`-as-repair. `IGeometryOperations` also
carries two different meanings under one name: its `Simplify` is
Douglas-Peucker generalization, while GeoServices `simplify` is topological
repair.

The geoservices-implementation-plan §5 leaves the interface granularity open
between "one extended `IGeometryOperations`" and split faces.

## Decision

Add three focused SDK interfaces over core geometry values, implemented by
`Spatial.Operations.NetTopologySuite`:

- `IGeometryMeasures` — `Area`, `Length`, `Distance`, `LabelPoint`.
- `IGeometryProcessing` — `Union`, `Difference`, `ConvexHull`, `Repair`,
  `Densify`. `Repair` is the topological MakeValid-equivalent; it is
  deliberately distinct from `IGeometryOperations.Simplify`.
- `IGeometryRelations` — `Relate` (DE-9IM intersection pattern).

The faces stay granular rather than growing `IGeometryOperations` so that an
implementation advertises exactly the capability it owns, and so the
adapter's `as`-free composition mirrors the granularity. All verbs are pure,
planar and cancellable, and all accept and return only `Spatial.Core`
geometry values (ADR-0005).

The GeoServices adapter maps:

| GeoServices | Verb |
| --- | --- |
| `areasAndLengths`, `lengths`, `distance`, `labelPoints` | `IGeometryMeasures` |
| `convexHull`, `difference`, `union`, `densify`, `simplify` (repair) | `IGeometryProcessing` |
| `relation` | `IGeometryRelations` |
| `project` | `ICoordinateTransforms` |
| `generalize`, `buffer`, `intersect` | `IGeometryOperations` |

## Consequences

- Three additive interfaces in `Spatial.PluginSdk` and three additive
  implementations in `Spatial.Operations.NetTopologySuite`, registered by
  `Spatial.Host`. No existing contract changes.
- The semantic trap is resolved structurally: `generalize` and `simplify`
  map to different engine verbs (`Simplify` and `Repair`), with an explicit
  regression test that a self-intersecting ring is repaired into valid parts
  while generalization leaves it alone.
- Repair uses NTS `GeometryFixer` (the OGC MakeValid port); it splits a
  self-intersecting ring into its valid parts.
- New verbs are still pure and synchronous; long work remains the caller's
  cancellable request (ADR-0033).

## References

- ADR-0033 (in-process service interfaces)
- ADR-0035 (GeoServices boundary adapter)
- `architecture/geoservices-implementation-plan.md` §5 (S1b)
- `architecture/distilled/contracts.md`
