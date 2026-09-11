# Spatial.Core

The spatial value model. This project is the kernel's kernel: no packages,
no project references, no algorithms (enforced by
`Core_has_no_dependencies` in tests/architecture).

## Inclusion test — a type belongs here only when

1. almost every spatial plugin must exchange it,
2. independently developed plugins must agree on its meaning, and
3. it can be represented without choosing a spatial algorithm.

Owned here: `Coordinate`, `CoordinateLayout`, `ICoordinateSequence`,
`Envelope`, `IGeometry` and simple-feature types, `CoordinateReference`, the
geometry contract faces `IPoint`, `ILineString`, `IPolygon`, `IMultiPoint`,
`IMultiLineString`, `IMultiPolygon`, `IGeometryParts` and `IGeometryFactory`
(ADR-0032; bind these where you only inspect or transport geometry),
`AttributeKind`, `AttributeValue`, `FeatureId`, `FieldDefinition`,
`IFieldDefinition`, `IFeature`, `IFeatureSchema`, `IFeatureBatch` (the
feature model's contract faces, ADR-0029) and the concrete
`FieldDefinition`, `FeatureId`, `FeatureSchema`, `Feature`, `FeatureBatch`;
canonical binary encoding (Phase 1: `GeometryCodec` v1 — spec in
`architecture/distilled/core.md`, living in `Spatial.Core.Geometry.Codec`
per ADR-0032; Phase 2: `FeatureBatchCodec` v1 — spec in
`architecture/distilled/core.md`, living in `Spatial.Core.Features.Codec` per
ADR-0029), `GeometryFactory`, `GeometryTraversal`.

## Never here

Buffer, intersection, union, predicates, distance, area, length, centroid,
simplification, validation/repair, transformation, indexing, SQL/store
behaviour, rendering, tiling, persistence, workflow. Those ship in implementation projects
under `src/Spatial.Operations.*`, `src/Spatial.Provider.*`, etc. (ADR-0033).

## Rules

- Values are immutable (ADR-0004); builders create new values.
- Large coordinate sequences use packed storage — never one heap object per
  coordinate.
- Types are structural: inspection, enumeration, traversal, envelopes from
  minima/maxima, canonical round trips, structural equality.
- No NetTopologySuite, Npgsql or any third-party package, ever (ADR-0005).