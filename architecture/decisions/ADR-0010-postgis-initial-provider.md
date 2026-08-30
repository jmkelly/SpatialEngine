# ADR-0010: Initial backing provider is PostGIS

Status: Accepted

## Context

The first vertical slice needs one real data store to prove the provider
contracts, streaming and writing paths.

## Decision

PostGIS is the initial backing provider, implementing the catalogue,
dataset and feature capability contracts, with host-managed connection
secrets, bounded streaming and database-command cancellation.

## Consequences

- Provider contracts are proven against real-world behaviour early.
- Integration tests run PostGIS in a container (local dev profile).
- Other stores (GeoParquet, COG, OGC APIs) follow the same contracts later.