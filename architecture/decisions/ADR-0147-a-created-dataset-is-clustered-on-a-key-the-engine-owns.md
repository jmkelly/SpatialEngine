---
status: accepted
date: 2026-10-01
deciders: maintainer + agent
summary: **A SQL Server dataset created by `IDataCatalogue.CreateAsync` is clustered on a key the engine owns and the contract never sees**: the create statement declares a `bigint IDENTITY(1,1)` primary key under a constraint name derived from the table, because SQL Server will not build a spatial index on a table it cannot cluster, and the key is what makes ADR-0092's spatial index exist on a created table at all. The two schema reads leave a column keyed that way out of the columns and the primary key they report, so a created dataset keeps the **keyless** shape ADR-0131 and ADR-0140 are written against and does not grow a field or a feature identity a created PostGIS dataset does not have. The key follows `SqlServerOptions.CreateIndexes`: with index creation off there is nothing to grid, so the table is the one ADR-0092 shipped. The column takes the first name the creating schema is free of, so no schema is refused for a name the contract never reserved. No contract type changed (amends 0092).
amends: ADR-0092
---

# ADR-0147: A created dataset is clustered on a key the engine owns

## Context

ADR-0092 gave a created dataset its indexes, and named the one its SQL Server
plan could not deliver. SQL Server builds a spatial index by laying the grid's
cells in **clustering order**, so it refuses to build one on a table that has
no clustered primary key — and a table made by `IDataCatalogue.CreateAsync` has
no primary key at all. The plan therefore asked for attribute btrees and
nothing else, and a bounding-box pushdown over a created dataset was a
clustered index scan. The ingest path never had the problem, because
`IngestIdentity.Auto` gives its table an `IDENTITY` primary key.

`Create_indexes_the_attribute_columns_the_server_can_key`
(`tests/integration/Spatial.SqlServer.Tests/SqlServerIndexIntegrationTests.cs`)
asserted the limitation rather than assuming it: it read the catalogue and
found no spatial index on a created table's geometry column, and the test above
it asserted that the pushdown over a created table never named one.

The fix is one column, and the bead that found this was right that it is not
just a DDL change. Adding an `id` to a created table **changes what a created
dataset is**: `DescribeAsync` discovers its columns from the catalogue, so the
dataset would gain a field no client supplied, and its primary key would become
a feature identity — which would make `IFeatureLookup` and the edit session
answer on a created table where they are refused today (ADR-0140), and would
make a created SQL Server dataset shaped like nothing a created PostGIS
dataset is, under one contract, for one provider's storage engine. PostGIS has
no such limit at all: `CREATE INDEX ... USING GIST` needs no primary key, so
its created tables have always carried one.

So the question this record answers is not "how does the create statement get a
key" but "**who is the key for**".

## Decision

**A created table is clustered on a key the engine owns, and the contract does
not see it.** The key is the engine's answer to the engine's own limit, and it
is taken back out of everything the contract reads.

### 1. The create statement declares it

`SqlServerCreatePlan` (pure, no SqlClient, like `SqlServerIngestPlan`) plans a
create as ADR-0092 left it plus one column:

```sql
CREATE TABLE [dbo].[cities] ([population] bigint, [geom] geometry,
  [id] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [spatial_key_cities] PRIMARY KEY)
```

`IDENTITY` because a client must not have to supply a value: an appended batch
names only the columns it carries, so the database assigns the key and nothing
in the write path has to learn about it. The key is created **inside the create
transaction** with the table and its indexes, so there is no window in which a
created dataset exists unclustered, and a `CREATE TABLE` that cannot be gridded
rolls back like any other failed create (ADR-0092 §4).

`SqlServerIndexPlan` then emits the spatial index, because the plan is told
there is a clustered primary key — the one condition ADR-0092 had to check and
could never satisfy on this path.

### 2. The contract does not see it, and the two reads are where that happens

`SqlServerQueries.ColumnsMetadata` and `PrimaryKeyColumns` each take the
constraint name the key was declared under, and leave a column keyed that way
out. The name is **derived from the table** (`spatial_key_<table>`, shortened
deterministically past SQL Server's 128-character identifier limit with a
digest, as `SqlServerIndexName` does for indexes) rather than fixed, because a
constraint name is unique per schema: a fixed name made the second created
dataset in a schema fail with "There is already an object named
`spatial_engine_key` in the database" — found by the integration suite, not by
reasoning.

So `DescribeAsync` answers exactly what it answered before: the same columns in
the same order, the same empty `IdColumns`, the same row estimate. A created
dataset stays the keyless dataset that ADR-0131's conformance fixture and
ADR-0140's refusal are written against, and every read, every pushed plan and
every published service is unchanged because every one of them reads the
discovered description.

An **ingested** dataset is unaffected: its own primary key is not declared under
this name, so nothing is filtered and its identity stays the identity
(ADR-0041). The distinction is the constraint name, which is why the name is
derived from a validated table identifier and is never a value a caller
supplies.

### 3. The column takes a name the schema is free of

`id` is the name the ingest path's `IngestIdentity.Auto` already uses, and
`spatial_key_<table>` is the constraint. A creating schema is the client's own —
the shared conformance fixture has an `id` of its own — so the plan takes the
first name the schema does not declare: `id`, then `id_1`, then `id_2`, and so
on, compared **case-insensitively** because the name must not collide with a
column under the database's collation, which usually folds case.

Refusing a schema for a name the contract never reserved was considered and
rejected: it would have made `CreateAsync` fail on an ordinary schema with an
`id` field, which is a contract change in the direction this record exists to
avoid. A name nobody can collide with does not exist; a name the engine can find
a free one for does.

### 4. The key follows the opt-out

`SqlServerOptions.CreateIndexes` off means no index is built, so there is
nothing to grid, so the key is not added and the created table is the one
ADR-0092 shipped — a heap with attribute btrees and no spatial index. The key
exists for one index, and it does not outlive the switch that says whether that
index is wanted. The rule is stated in the plan rather than in the catalogue,
so `CreateAsync` has one decision and not two.

## Consequences

- A bounding-box pushdown over a created SQL Server dataset seeks a spatial
  index, which is the whole of ADR-0092's promise on this path; the plan is
  asserted through `SET STATISTICS XML` in
  `A_created_dataset_pushdown_seeks_the_spatial_index`, and the index is
  asserted from the catalogue in
  `Create_indexes_the_attribute_columns_the_server_can_key` — the test that
  previously asserted its absence.
- A created table is physically different: an operator looking at the database
  sees a `spatial_key_<table>` constraint on an `id` column, and the table is a
  clustered table rather than a heap. That is the trade: the layout is the
  provider's, and it is the provider's business what the layout is. The DDL was
  always the provider's; this record says so about one more column.
- `Spatial.Contracts` did not change: no contract type gained or lost a member,
  and no created dataset answers a read differently. A dataset's *shape* is
  unchanged; its *cost* is different, which is the same trade ADR-0092 made.
- A created table cannot be created through a schema that names every candidate
  key name, which is a pathological schema and is not worth a rule of its own.
- The pure plan is unit-tested without a server
  (`SqlServerCreatePlanTests`), so "the created table carries a clustered key",
  "the key is what makes the dataset gridable" and "the opt-out drops the key
  too" are tests rather than claims.

## References

- ADR-0041 (ingest and the identity modes — the same key, the other create path)
- ADR-0092 (a created dataset carries its own indexes; this record answers the
  follow-up it recorded)
- ADR-0097 (the pushdown the spatial index serves)
- ADR-0131 (a pushed read names a row by the key it read — the keyless fixture)
- ADR-0140 (a read-by-identity needs a declared identity column — why the key
  stays out of `IdColumns`)
