# Spatial.Core

The spatial value model. This project is the kernel's kernel: no packages,
no project references, no algorithms (enforced by
`Core_has_no_dependencies` in tests/architecture).

## Inclusion test — a type belongs here only when

1. almost every spatial plugin must exchange it,
2. independently developed plugins must agree on its meaning, and
3. it can be represented without choosing a spatial algorithm.

Owned here: `Coordinate`, `CoordinateLayout`, `ICoordinateSequence`,
`Envelope`, `IGeometry` and simple-feature types, `CoordinateReference`,
`Feature`/`FeatureId`/`AttributeValue`/`FieldDefinition`/`FeatureSchema`,
canonical binary encoding (from Phase 1 onward).

## Never here

Buffer, intersection, union, predicates, distance, area, length, centroid,
simplification, validation/repair, transformation, indexing, SQL/store
behaviour, rendering, tiling, persistence, workflow. Those ship in plugins
under `src/Spatial.Operations.*`, `src/Spatial.Provider.*`, etc.

## Rules

- Values are immutable (ADR-0004); builders create new values.
- Large coordinate sequences use packed storage — never one heap object per
  coordinate.
- Types are structural: inspection, enumeration, traversal, envelopes from
  minima/maxima, canonical round trips, structural equality.
- No NetTopologySuite, Npgsql or any third-party package, ever (ADR-0005).