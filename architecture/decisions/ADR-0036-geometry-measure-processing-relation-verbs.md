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
`densify` and `relation`. `IGeometryOperations` carries the one verb two
GeoServices names share: both `generalize` (§7.0.13) and `simplify`
(§7.0.5) are Douglas-Peucker generalization, and its `Simplify` is exactly
that. Topological repair is wanted too — no Esri operation names it, so
nothing in the protocol maps to it.

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
| `convexHull`, `difference`, `union`, `densify` | `IGeometryProcessing` |
| `relation` | `IGeometryRelations` |
| Feature Service `spatialRel` (`Contains`/`Within`/`Touches`/`Overlaps`/`Crosses`) | `IGeometryRelations` (DE-9IM patterns, envelope-prefiltered) |
| `project` | `ICoordinateTransforms` |
| `generalize`, `simplify` (`deviation`/`value`), `maxAllowableOffset`, `quantizationParameters`, `buffer`, `intersect` | `IGeometryOperations` |

The Geometry Service's two operations take an algorithm tolerance and go
through `Simplify`; the Feature Service's two query parameters state a
deviation budget and go through `Generalize` (ADR-0079). The split is
deliberate: a tolerance the caller picks for the algorithm and an allowance
the caller grants the answer are not the same number, and one method cannot
carry both without the two being swapped at a call site.

## Consequences

- Three additive interfaces in `Spatial.PluginSdk` and three additive
  implementations in `Spatial.Operations.NetTopologySuite`, registered by
  `Spatial.Host`. No existing contract changes.
- The name trap is resolved structurally: `generalize` and `simplify` both
  call `Simplify`, each with its own tolerance parameter (`maxDeviation`,
  `deviation`/`value`), with a regression test that a self-intersecting ring
  is thinned rather than repaired.
- `Repair` stays an engine verb with no protocol name. It uses NTS
  `GeometryFixer` (the OGC MakeValid port) and splits a self-intersecting
  ring into its valid parts, reachable through the engine API only: an Esri
  operation that aliased it to `simplify` would generalize nothing and
  repair instead, which is what the wiring used to do.
- New verbs are still pure and synchronous; long work remains the caller's
  cancellable request (ADR-0033).
- `Relate` is the one face the query path uses as well as the geometry
  service: the Feature Service `spatialRel` verbs are its DE-9IM patterns
  (`T*****FF*` contains, `T*F**F***` within, `F***T****` touches, and the
  point, overlaps and crosses variants selected by geometry dimension),
  with the envelope tests kept as the pre-filter so the exact predicate runs
  only on candidates. No contract change — the interface was already
  registered; only the adapter's dependency set grew.

## References

- ADR-0033 (in-process service interfaces)
- ADR-0035 (GeoServices boundary adapter)
- `architecture/geoservices-implementation-plan.md` §5 (S1b)
- `architecture/distilled/contracts.md`
