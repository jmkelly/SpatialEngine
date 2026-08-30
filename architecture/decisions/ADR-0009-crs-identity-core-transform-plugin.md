# ADR-0009: CRS identity is core; transformation is a plugin

Status: Accepted

## Context

Every geometry needs to state which coordinate reference system it uses, but
transforming between CRSs is a heavy domain of its own.

## Decision

`CoordinateReference` (authority + code) is part of the core value model.
Coordinate transformation is a capability plugin
(`spatial.coordinate.transform@1`) implemented by a transformation library
adapter.

## Consequences

- CRS identity is unambiguous everywhere, with no algorithm in the core.
- Transformation libraries can be swapped behind the contract.
- Axis order, error and tolerance behaviour are tested per provider.