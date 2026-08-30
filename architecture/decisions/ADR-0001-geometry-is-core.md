# ADR-0001: Geometry is part of the spatial core

Status: Accepted

## Context

Nearly every spatial plugin must exchange geometry values, and independently
developed plugins must agree on their meaning. Geometry can be represented
structurally (coordinates, sequences, types, envelopes) without choosing any
spatial algorithm.

## Decision

`Spatial.Core` owns an immutable geometry value model: `Coordinate`,
`CoordinateLayout`, `ICoordinateSequence`, `Envelope`, `IGeometry`, the
simple-feature geometry types and canonical binary encoding. Spatial
algorithms (buffer, intersect, transform, validation) are never implemented
in the core.

## Consequences

- Every plugin and every future client shares one stable geometry vocabulary.
- Core stays small and independently testable (enforced by architecture tests).
- Algorithm evolution happens behind capability contracts, not type changes.