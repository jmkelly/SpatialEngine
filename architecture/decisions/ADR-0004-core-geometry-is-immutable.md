# ADR-0004: Core geometry is immutable

Status: Accepted

## Context

Geometry values travel between the core, workers, providers and clients.
Mutable values invite aliasing bugs across process boundaries and make
caching and concurrency unsafe.

## Decision

All core geometry values are immutable. Builders and factory methods produce
new values; there is no in-place mutation API.

## Consequences

- Values can be shared, cached and passed across boundaries safely.
- Construction-heavy pipelines require allocation discipline (tracked by
  allocation tests in Phase 1).
- Structural equality is defined for all core geometry types.