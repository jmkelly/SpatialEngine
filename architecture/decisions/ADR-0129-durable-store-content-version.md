---
status: accepted
date: 2026-09-30
deciders: maintainer + agent
---

# ADR-0129: A durable store's content version is a row in its own database

## Context

ADR-0083 added `IVersionedFeatureStore` and folded the token it reports into
every tile cache key, so a write invalidates exactly the tiles derived from the
data it changed. It shipped the contract and the in-memory provider's
counter, and named the rest of the gap in its consequences: "A store that
reports no version (PostGIS, SQL Server, demo, ArcGIS REST) keeps its current
behaviour, which means a write to a durable store from *this* process also
still leaves its tiles stale until a flush. Closing that needs a durable
version source (a trigger-maintained version table, a transaction id,
`xmin`), which is a store-level decision tracked as a follow-up, not a change
to this contract."

This is that decision. The constraint that makes it more than a counter is
stated in the bead: *an in-process counter is not enough for a durable store*.
The read path and the write path of each SQL provider are **separate DI
singletons** — `PostgisStore` / `SqlServerStore` on the read side,
`PostgisEditStore` / `PostgisIngestStore` (and the transaction entries behind
`PostgisTransactions` / `SqlServerTransactions`) on the write side — and a
deployment runs more than one host against one database. A version any of
those holds in memory is correct on one process and wrong on the next, and the
failure mode is silent: the key is stable, the cache serves a tile drawn from
data that no longer exists.

So the version has to be a fact the *database* holds, and it has to move in the
same transaction as the write — or a rolled-back write would move it, the
cache would miss for nothing, and worse, a rolled-back write inside a
transaction handle would leave the key and the data disagreeing about which
world is true.

## Decision

**A per-dataset counter row, written by the write paths inside the write's own
transaction, and read fresh by the read path on every call.** The rule is one
rule; where each provider puts the row follows that provider's own metadata
conventions.

**PostGIS: one `spatial_dataset_version` table per dataset schema**, holding
`(dataset text PRIMARY KEY, version bigint NOT NULL)`, created
`IF NOT EXISTS` on the write path. Per schema, not one database-wide table,
because a PostGIS dataset is addressed as `schema.table` and the engine may
have rights in the dataset's own schema and none in `public`; the row then
needs no privilege the write itself did not need, and dropping the schema drops
its versions. The bump is a single
`INSERT … ON CONFLICT DO UPDATE SET version = … + 1`, so two writers never
read-then-write.

**SQL Server: one `spatial_dataset_versions` sidecar table**, holding
`(dataset nvarchar(300) NOT NULL PRIMARY KEY, version bigint NOT NULL)`,
keyed by the dataset's *qualified* name and created under the
`IF OBJECT_ID(…) IS NULL` guard its SRID sidecar (`spatial_datasets`,
ADR-0073) already uses. A single sidecar, because a SQL Server dataset may
live in a schema the engine never created anything in, so a per-schema table
would ask for a privilege the write did not need. The bump is
`UPDATE … ; IF @@ROWCOUNT = 0 INSERT …` in the write's transaction rather
than `MERGE`, which would say it in one statement and has known correctness
bugs.

**The read never issues DDL.** PostGIS's read maps `undefined_table` and
`invalid_schema_name` to the unversioned token; SQL Server's read statement
guards itself on the sidecar's existence and returns no row. So a read-only
connection still answers, and a dataset the engine has **never written**
reports `ContentVersions.Unversioned` — exactly the token and exactly the
behaviour ADR-0083 gave every store before this one. Nothing regresses for a
read-only deployment, and an externally-seeded table costs nothing until it is
written. The read also maps `insufficient_privilege` to the unversioned token:
a role granted `SELECT` table by table rather than through a schema grant can
read a dataset it never wrote, and a render must not fail because it could not
read a cache-invalidation side table. Degrading is the pre-ADR behaviour, which
is no worse than before.

**Every write path bumps; nothing else does.** `IFeatureStore.WriteAsync`
(autocommit and transaction-handle), `IFeatureEditStore` add / update /
delete, `IDatasetIngest` and `IDatasetIngestStream`, and
`IDataCatalogue.CreateAsync` — matching the set ADR-0083 fixed for the
in-memory provider. The bump is placed *inside* the transaction the rows were
written in, in every one of them, so a rollback restores the features and the
version together. Two rules carry over from ADR-0083 unchanged: an edit batch
moves the version when it changed **at least one** feature (a partially
applied batch still changed something; a batch where every feature failed
changed nothing), and attachment writes do not move it, because nothing
renders an attachment into a tile.

**No new process state.** The provider's version helper is a set of stateless
statements, not an object holding a version, and there is deliberately no
"have I created the table yet" flag: the create is idempotent and cheap, and a
flag is precisely the kind of per-singleton fact that drifts between the read
path and the write path. The one thing the read path caches is nothing.

**A store failure to bump is a store failure.** If the counter row cannot be
written, the write path reports `store.unavailable` rather than continuing
with a version it cannot move. That is the honest outcome, and it is rare in
practice, because the counter lives where the write already needed rights: a
schema the engine can write into, or a table the first write created.

## Consequences

- A write to a PostGIS or SQL Server dataset now misses the tile cache on the
  host that made it **and on every other host reading the same database**, with
  no `DELETE /api/render/cache`. ADR-0083's stated gap is closed for both
  durable providers; the demo and ArcGIS REST stores stay unversioned, which is
  correct — nothing writes to them through the engine.
- One extra small indexed statement per write, and one per layer per tile
  request. The read is deliberately not cached in process: a cached version is
  the bug this ADR exists to prevent. The tile key already costs one version
  resolution per distinct layer per request (ADR-0083), so this is the same
  shape of cost against a durable store instead of a dictionary.
- A new engine-owned object appears in the database: `spatial_dataset_version`
  per PostGIS schema, and `spatial_dataset_versions` for SQL Server. Operators
  that enumerate or dump a schema see it, and it is named
  `spatial_*` alongside the tables ADR-0073 already keeps. It is created by the
  first write, not by the first read, so a database the engine has only read
  is untouched.
- A dataset created outside the engine and never written through it reports the
  unversioned token, so its tiles are still only invalidated by a flush. That
  is unchanged behaviour, and it is the honest one: the engine cannot know
  about a write it did not make — unless the write is made through the engine,
  which is the case this ADR covers.
- A write outside the engine (psql, another service, `ogr2ogr`) still does not
  move the counter. A database trigger would cover that case, and is
  deliberately **not** chosen: the engine does not put triggers on tables it
  did not create, and per-dataset trigger installation on every write is a
  privilege and a permanence the caller did not ask for. This is the residual
  gap, and it is a gap in the *source* of the write, not in the mechanism.
- `IVersionedFeatureStore` is unchanged. No contract type, no package and no
  DI registration moved: the two providers simply implement a face they
  already satisfied structurally. `Spatial.Contracts` still references only
  `Spatial.Core` and takes no package, and the host stays JIT-compiled — no
  Native AOT surface was added.

## Alternatives

- **A trigger-maintained version table.** Would also catch writes made outside
  the engine. Rejected: the engine would install a trigger on every table it
  writes, including tables it did not create, and a trigger the caller cannot
  see or remove is a permanence a cache-invalidation nicety does not justify.
  Worth revisiting if a deployment genuinely writes through several tools.
- **A transaction id / `xmin`-style stamp** (the PostgreSQL route: read the
  relation's own `xmin` or the max `xmin` over the rows). Rejected: it is not
  per dataset (a single stamp over the store over-invalidates every map, which
  is the outcome ADR-0083 rejected for a per-map key), and in SQL Server it
  needs a `rowversion` column the caller's tables do not have.
- **Hash the dataset's content on every tile request.** Correct across
  processes and already rejected in ADR-0083: a tile would read every feature
  it draws.
- **One counter per store rather than per dataset.** Cheaper to resolve, and
  re-invalidated by any edit anywhere — the same over-invalidation ADR-0083
  rejected.
- **Keep the counter in the host, bumped by the write endpoints.** Correct on
  one process, wrong on the second, and wrong after a restart. This is the
  failure the bead names, and the reason the decision is store-level.
- **A PostgreSQL `LISTEN`/`NOTIFY` fan-out to warm or drop cache entries.**
  Complementary, not a substitute: it is an optimisation on top of a correct
  key, and it does not survive a host that missed the notification.

## References

- ADR-0083 (the tile cache key carries the data version; the gap this closes)
- ADR-0028 (the PostGIS provider; SQL, Npgsql and the identifier grammar)
- ADR-0073 (the SQL Server provider; the `spatial_datasets` SRID sidecar)
- ADR-0037 (the store-side edit face, and the session the bump joins)
- ADR-0041 (ingest, and the one transaction the bump joins)
- ADR-0042 (the in-memory provider whose counter this mirrors)
- ADR-0117 (per-layer tile composition, which bounds what one edit invalidates)
