---
status: accepted
date: 2026-09-13
deciders: maintainer + agent
---

# ADR-0042: An ephemeral writable in-memory provider enables database-free ingest and publish

## Context

ADR-0041 delivered protocol-neutral ingest and publication capabilities but
deferred one decision: the database-free profile. The repository's promise
("start without a database") currently holds for *reading* the demo store
(`Spatial.Provider.Demo`), which is read-only by design. With the neutral
host API (`POST /api/ingest`, `PUT /api/publications`) the workbench's
upload → publish → query flow would require PostGIS, contradicting the
database-free start and making the feature undemonstrable in CI without
Docker.

The demo provider cannot simply be made writable. It is a read-only vehicle
for deterministic browsing (`AGENTS.md` security model: "The demo store is
read-only; writes/creation/transactions against it are `invalid.arguments`"),
and its datasets are procedural (a generated grid, committed snapshots), not
a general table store. Making it writable would blur that boundary and change
its contract.

## Decision

**1. A first-class, ephemeral writable in-memory provider, keyed `memory`.**

A new implementation project (`Spatial.Provider.Memory`) implements the
writable engine faces over an in-process, dictionary-backed table store:

- `IDataCatalogue`, `IFeatureStore`, `IFeatureEditStore`, `IFeatureLookup`,
  `ITransactionStore`, `IDatasetIngest`.

It is registered in `Spatial.Host` under the key `memory`, always available
like `demo`, so `POST /api/ingest?store=memory` and
`PUT /api/publications` work with no external database. It is intentionally
distinct from the demo store: `demo` stays read-only and procedural.

**2. Ephemeral means non-durable, and that is a documented property.**

State lives only for the process lifetime; restart loses datasets and
transactions. It is a development-and-CI provider, not a persistence
guarantee. Diagnostics state this explicitly so a client cannot mistake it
for Durable storage. It is not a substitute for PostGIS in production.

**3. The same identity and atomicity rules as PostGIS ingest.**

`IDatasetIngest` honours `IngestIdentity.None|Auto|Source` with the same
semantics as the PostGIS ingest store (ADR-0041 §3): `Auto` assigns an
integer identity to every loaded feature, `Source` uses a named integer
field as the key, `None` loads a data-only table. Create-and-load is one
operation (no partial dataset is observable). Editing (ADR-0037) and
read-by-identity (ADR-0038) are available for `Auto`/`Source` datasets and
rejected for `None`.

**4. It stays behind the SDK boundary.**

The provider references `Spatial.Core` and `Spatial.PluginSdk` only, takes
no packages, and adds the usual guard entries (`PlatformProjectNames`,
`ImplementationProjectNames`, host `allowed`). No store-specific type
crosses any contract.

## Consequences

- The database-free start now covers the full ingest → publish → serve path,
  so the workbench and the host HTTP tests can exercise it without Docker.
- `Spatial.Host` registers a second always-available store; the
  `/health/ready` store list and the store resolver gain `memory`.
- The demo store is unchanged and remains the deterministic read-only
  browsing vehicle; the two providers do not overlap in responsibility.
- Restart durability remains PostGIS's job. A process restart is a lossy
  event for `memory` by design, documented at the option and in diagnostics.
- Memory use is bounded by the hosted data; the ingest caps (`MaxBytes`,
  `MaxFeatures`, ADR-0041 §6) remain the guard rail.

## References

- ADR-0041 (ingest and publications are protocol-neutral)
- `architecture/publishing-and-ingest-plan.md` (P2b)
- ADR-0037 (feature editing), ADR-0038 (read-by-identity)
- `architecture/distilled/contracts.md`, `host-and-clients.md`
