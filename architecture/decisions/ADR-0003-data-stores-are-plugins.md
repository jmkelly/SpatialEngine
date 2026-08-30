# ADR-0003: Data stores are capability plugins

Status: Accepted

## Context

PostGIS, GeoParquet, COGS and future stores differ in behaviour, dialect and
performance characteristics. A single large `ISpatialDataStore` interface
couples the runtime to every store's union of features.

## Decision

Data stores are provider plugins implementing small capability contracts
(`spatial.catalogue.list@1`, `spatial.feature.scan@1`, `spatial.feature.write@1`,
…). Providers may push down operations but must implement the same capability
contracts and pass the same conformance tests.

## Consequences

- Stores are added and swapped without runtime changes.
- Pushdown is optional and preserves contract semantics.
- The host manages connection secrets; providers receive scoped access only.