---
status: accepted
date: 2026-09-28
deciders: maintainer + agent
---

# ADR-0092: A created dataset carries its own indexes

## Context

Both SQL stores had a `CreateAsync` that built the result table and stopped.
`PostgisStore.CreateAsync` and `SqlServerStore.CreateAsync` issued a
`CREATE TABLE`, recorded the SRID, and committed — no GiST index on the
geometry column, no btree on anything. The ingest paths did the same.

That is invisible until something asks the table a question the database could
answer from an index. The pushdown query surface (ADR-0097, ADR-0098) is
exactly that question, and the measurement in
`eng/spike-u2x-query-baseline/RESULTS.md` (finding 7) is what it costs. On the
34,135-row world-cities table, a bounding box plus a `where` was a **Seq
Scan**; after `CREATE INDEX ... USING GIST (geom)`, `btree(population)`,
`btree(country)` and an `ANALYZE`, the same statement became a **BitmapAnd of
two index scans** and p50 went 73 → 44 ms on the europe box and 170 → 109 ms
on the global box.

The bead that found this named the real question as *where does index
provisioning belong*: the store's create/ingest faces, the host's publish flow,
the feature-query ADR, or the operator. The last option is the one that was
already happening and is the one this record rejects.

## Decision

**A dataset created or ingested by a SQL store carries its spatial and
attribute indexes from the commit that creates it, and the store's DDL
statement is the only place that says so.**

### 1. Index provisioning belongs to the store's create and ingest faces

Not the host's publish flow, and not a step the operator has to remember. The
reason is the one ADR-0097 already established for pushdown: the store is the
thing that knows the column layout, the SRID, the identity columns and the
provider's type mapping, and it is the only place where "a created dataset is
queryable" can be a property of *creating* rather than a convention somebody
agrees to follow afterwards. `eng/seed.sh` needs no new flag, because there is
no new step. A dataset is not born unindexed and later repaired; it is never
unindexed.

Publish-time indexing was considered and rejected for the same reason it fails
in practice: it runs for the datasets somebody published and not for the ones
somebody created, which is how half-indexed tables get made.

### 2. The plan is a pure, testable function of the schema

`PostgisIndexPlan.CreateIndexes` and `SqlServerIndexPlan.CreateIndexes` take
the dataset name and the schema and return the `CREATE INDEX` statements. No
connection, no provider types, deterministic for equal inputs — so the DDL a
dataset gets is unit-tested without a database, and the same statement is
asserted through `EXPLAIN` against the real container in the integration
suite. Every identifier in it comes from a validated dataset name and a
schema-validated field name, never from client text.

### 3. What gets an index, and what does not

- **The primary geometry column** — the first geometry field in column order,
  which is the same rule schema discovery applies to a table read back from the
  database. PostGIS gets `USING GIST`; SQL Server gets `CREATE SPATIAL INDEX`
  with the `*_AUTO_GRID` form, so the server derives the grid from the data and
  the store never has to know an extent (including when the table is still
  empty).
- **Every attribute column** — because a pushed-down filter may name any field
  of the schema, and an unindexed one is a scan. A column the create statement
  already carries a primary key on is skipped: a second index on the same
  column is cost without benefit.
- **Not a SQL Server text column.** Every string column is created as
  `nvarchar(max)`, which SQL Server refuses as an index key. The provider
  detects that from its own type mapping (`IsIndexableKind`) rather than
  discovering it as a failed `CREATE INDEX`, and the consequence — a filter on
  a text column is not index-served on this provider — is asserted rather than
  left for someone to find in a plan.

Index names are derived from the table and column (`ix_<table>_<column>`), so
a dataset's indexes are predictable, and are shortened deterministically past
each engine's identifier limit (63 bytes on PostgreSQL, 128 characters on SQL
Server) with a digest of the full name appended, so two long columns sharing a
prefix get distinct names rather than a silent server-side truncation
collision.

### 4. The indexes are part of the create transaction

The table and its indexes are one transaction, in the catalogue path and in the
ingest path alike. A dataset whose indexes cannot be created does not exist:
the create rolls back and the store reports `store.unavailable`, rather than
leaving a table that the planner sequential-scans and calls a success. A
cancelled create likewise leaves nothing.

The indexes are created **before** the rows are loaded, not after. That costs
write amplification on a bulk load, and it buys the property that matters more
here: there is no window in which the dataset exists and is unindexed, not even
inside its own transaction.

### 5. The operator can turn it off, and that is all

`PostgisOptions.CreateIndexes` and `SqlServerOptions.CreateIndexes` default to
`true` and are the documented escape hatch for a bulk load that would rather
build the indexes itself afterwards. The default is on, because the failure
mode of the default is silent and the failure mode of the flag is a dataset
whose owner asked for it. Turning it off is a choice with a name and a
setting, not the absence of a step.

## Consequences

- A dataset that has been created is queryable. `eng/seed.sh` and any other
  caller get an indexed table from `CreateAsync` with no out-of-band DDL, and
  the pushdown queries of ADR-0097 land on index scans.
- The DDL a dataset gets is a pure function of its schema and is unit-tested
  without a database; the plan is asserted against the real containers through
  `EXPLAIN` / `SET STATISTICS XML`, so "the planner uses the index" is a test
  and not a claim.
- Index creation is now on the create path, so a large ingest pays for it. The
  opt-out is the operator's lever, and the ADR states the cost rather than
  hiding it.
- A dataset that cannot have an index the engine wants is a real, named
  limitation rather than a `CREATE INDEX` that failed at 3am: SQL Server's two
  limits (no clustered primary key, no `nvarchar(max)` key) are detected in
  advance, and the missing clustered primary key is followed up as
  SpatialEngine-9vg.
- The stores' create faces changed behaviour, so `Spatial.Contracts` did not:
  no contract type gained or lost a member, because a created dataset's
  *shape* is unchanged. Its *cost* is different.

## References

- ADR-0028 (parameterised attribute filtering — the btree indexes serve it)
- ADR-0037 (store-side edit sessions — the transaction this reuses)
- ADR-0074 (the `WHERE` fragment the index plan serves)
- ADR-0090 (an upload is staged, then loaded resumable — the ingest path)
- ADR-0097 (pushdown identity and literal binding)
- ADR-0098 (store query surface — the surface that makes the indexes worth
  having)
- `eng/spike-u2x-query-baseline/RESULTS.md`, finding 7 (the measurement)
