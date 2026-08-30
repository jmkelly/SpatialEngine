# Core Boundary

Read when touching `src/Spatial.Core` or deciding whether something belongs
in the core. See implementation-plan.md §6 and ADR-0001.

## Inclusion test

A component belongs in the core only when ALL of the following hold:

1. Almost every spatial plugin must exchange it.
2. Independently developed plugins must agree on its meaning.
3. It can be represented without choosing a spatial algorithm.

## The core owns

- Spatial values: `Coordinate`, `CoordinateLayout`, `ICoordinateSequence`,
  `Envelope`, `IGeometry` and the simple-feature types, `CoordinateReference`,
  `Feature`, `FeatureId`, `AttributeValue`, `FieldDefinition`, `FeatureSchema`.
- Canonical encoding/decoding (ADR-0020).
- Structural runtime identity in the Runtime project: capability identity,
  plugin manifests, registry, routing, resource handles, streams, jobs,
  permissions, health, diagnostics, contract compatibility.

## The core never implements

Buffer, intersection, union, difference, predicates, distance, area, length,
centroid, simplification, validation/repair, coordinate transformation,
spatial indexing, SQL/store behaviour, rendering/styling/tiling, project
persistence, workflow or desktop-shell behaviour.

## Permitted structural behaviour

Type/empty-state inspection, coordinate enumeration and counts, layout
inspection, component traversal, envelope from coordinate minima/maxima,
canonical encoding/decoding, structural equality. Nothing algorithmic.

## Guards

- `Spatial.Core` has no packages and no project references — enforced by
  `Core_has_no_dependencies` in tests/architecture.
- Package allowlist keeps third-party types out of platform projects —
  `Platform_projects_use_only_allowlisted_packages`.