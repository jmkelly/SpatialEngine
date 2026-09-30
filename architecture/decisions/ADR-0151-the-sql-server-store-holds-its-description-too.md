---
status: accepted
date: 2026-10-01
deciders: maintainer + agent
amends: ADR-0122
summary: The SQL Server provider holds its discovered dataset description the way PostGIS does (ADR-0122), which is the record that deliberately left it out: `SqlServerDescriptionCache` sits on `SqlServerStorage` and is keyed by `SqlServerDatasetName`, so a walk of N pages over a layer costs one description read rather than N; every write the store makes forgets what it could have changed (create, ingest, append, edit, the end of a transaction, commit or rollback), a description that failed to be read is never remembered, an entry expires (`SqlServerOptions.DescriptionCacheTtl`, 30s, non-positive turns the cache off) so a schema changed outside the store is picked up rather than inherited, and the store reports the count the claim is measured by. The SQL Server store's invalidation points are its own files — its provider has its own transaction and ingest faces — and its stale-description cost is stated for its own discovery: a geometry type or SRID re-sampled, an identity column re-read.
---

# ADR-0151: The SQL Server store holds its description too

## Context

ADR-0122 recorded that every read face of the PostGIS provider describes its
dataset before it can compile any SQL, that describing runs five catalogue
queries, and that a walk of N pages therefore paid N of them to learn the same
schema N times. It fixed that for PostGIS and said, in its Consequences, that
"SQL Server has the same shape — `SqlServerCatalogue.DescribeAsync` runs its
catalogue reads per read — and is deliberately untouched here", tracked as its
own bead. This is that bead.

The shape is the same. `SqlServerCatalogue.DescribeAsync` opens a connection and
runs `ReadSchemaFactsAsync` (the column metadata, the primary-key columns and
the planner's row estimate) and `ReadGeometryFactsAsync` (the recorded SRID and
a sampled geometry value) to build one `DatasetDescription`. Every read face
asks for it first: `SqlServerFeatures.ScanAsync`/`QueryAsync`/`ByIdentityAsync`
and `SqlServerPlanReader.ReadAsync`/`CountAsync`/`DistinctAsync`/
`AggregateAsync`, plus the public `IDataCatalogue.DescribeAsync` behind all of
them. A paged walk over a layer paid one connection open and four to six
catalogue reads per page, to learn the same schema every time.

What differs is where the invalidation has to be written. The SQL Server store
is a separate provider with its own composition: `SqlServerStorage` owns the
pooled `SqlServerDataStore`, the catalogue face is `SqlServerCatalogue` and the
open transaction handles are `SqlServerTransactions` with an entry that was a
record until it had to carry state. The ingest, edit and write faces are
`SqlServerIngestStore`, `SqlServerEditStore` and `SqlServerStore` — their own
files, not PostGIS's.

The risk is not the hit rate. It is what a *held* description costs when the
table underneath it changes, which is the question ADR-0122 already answered
for the other provider and which has to be answered again in SQL Server's own
terms, because SQL Server discovers two things PostGIS reads from the catalogue.

## Decision

**1. `SqlServerStorage` holds the descriptions, keyed by
`SqlServerDatasetName`.** The cache sits on the thing that outlives a call,
because the faces are built per operation, and `SqlServerCatalogue.DescribeAsync`
asks it before it opens a connection. One description read per dataset per
lifetime-of-the-entry, and a walk of N pages issues O(1) of them, which
`SqlServerDescriptionCacheTests.A_paged_walk_reads_the_description_once_and_not_once_a_page`
measures as a count rather than in bytes: the walk is asserted to have read
every row in order first, so a store that answered cheaply by answering nothing
cannot pass it. The cache is an optimisation and never an answer; nothing
consults it except the describe path, and a miss is a miss.

**2. Every write the store makes forgets what it could have changed** — create
(`SqlServerCatalogue.CreateAsync`), ingest (both the batch and the streaming
face), append (`SqlServerStore.WriteConfiguredAsync`), edit (add, update and
delete alike) and the end of a transaction. Each forgets in a `finally`, so a
write that did not complete cleanly is not a write the store claims to know the
shape of, and a batch that applied some features and failed others still
forgets. `SqlServerTransactionEntry` is a class rather than a record because it
now records the datasets written through it, so forgetting at the end is
precise rather than a flush of everything.

Appending invalidating is the one that looks like overkill — a row does not
change a schema — and it is kept for the row estimate the description also
carries, which an append is exactly what moves.

**3. A transaction forgets on rollback as well as on commit**, and a commit
whose outcome is in doubt — one that threw — forgets too: the decision is taken
*after* any description read, so a description cannot be trusted to describe
the dataset across it.

**4. A description that failed to be read is never remembered.** A dataset that
is not a spatial dataset is `not.found` every time it is asked and a cancelled
read leaves nothing behind. A negative entry would be a claim that a dataset
does not exist, made by a read that could not answer, and the ingest that
creates the dataset is the caller that would inherit it.

**5. Out of band, a description expires.** The window is
`SqlServerOptions.DescriptionCacheTtl`, 30 seconds by default, and a
non-positive value turns the cache off — the store's earlier behaviour, and the
right setting for a database whose schema moves on a schedule this store cannot
see. There is no administrative cache-drop verb: the setting covers the same
ground without a new public surface.

**6. What a stale description may cost, in this provider's terms.** The two
failure cases ADR-0122 names are the common ones here and are structured
failures rather than wrong answers: a column **added** out of band is not in
the schema, so a plan naming it fails `invalid.arguments`; a column **dropped**
out of band is still in the schema, so the generated T-SQL names a column the
table no longer has and the server refuses it (`InvalidColumnName`,
`207`), surfaced as `store.unavailable`. The case that *is* a wrong answer is
this provider's own, and it is two fields wide: SQL Server keeps the SRID with
each value rather than on the column, so the description's geometry type and
SRID come from **sampling the dataset's first non-null value**, and a geometry
type changed or a different-SRID row inserted out of band is described by the
sample the store read until the entry expires. An identity column changed out
of band is the third: reads name the old key, and edits go to the old column.
All three are bounded to the window, and that is the cost this record accepts —
which is why the default is 30 seconds rather than "until the store restarts".
The row estimate is stale for the same window, and is an estimate either way.

**7. The store reports what it did.** `SqlServerDescriptionCache.Reads` counts
the descriptions that went to the catalogue and `Count` the ones held;
`SqlServerStore` surfaces both internally. A claim about round trips is only
worth making if it can be measured.

## Alternatives

- **Leave SQL Server as it is**, on ADR-0122's reasoning that a record is about
  one provider. Rejected: the defect is the same defect on the path a client
  walks to draw a layer, and "deliberately not fixed" is a cost, not a scope.
- **Cache forever, invalidate on write only, document that out-of-band DDL needs
  a restart.** Rejected for the reason ADR-0122 gives: it makes an `ALTER TABLE`
  invisible until someone restarts the host.
- **Validate the entry on every read** against a cheap token (a `data_modification_id`,
  a schema version) before answering. It removes the window and keeps the round
  trip the cache was introduced to remove — one per read instead of five is still
  O(N) on the walk.
- **Negative caching** for a dataset that does not exist, to spare a repeated
  `not.found`. Rejected for ADR-0122's reason, which is unchanged by provider:
  an entry is a claim, and the ingest that creates the dataset is the caller
  that would inherit the wrong one.
- **A cache shared across store instances** (a static, a distributed cache). Not
  adopted here either; it needs a cross-process invalidation story neither
  provider has.

## Not decided

- **Whether the description cache and the collation cache (ADR-0121) belong in
  one per-store object.** SQL Server now has the same pair PostGIS has — two
  per-store properties of the database, held in the same place, with the same
  "read once, drop on a failed probe" discipline — and ADR-0122 said a third
  such record should say whether they belong together. That record has not been
  written; this one does not take the decision.
- **Caching the catalogue listing** (`ListAsync`), which still goes to the
  database on every call.
- **A cache shared across hosts.** Single-process caching is the honest scope for
  a cache whose invalidation story is the writes *this* store makes.

## Consequences

- A paged walk, a count, a distinct, a grouped reduction and the served layer
  metadata all stop paying per-read catalogue discovery, on the SQL Server
  provider as well as the PostGIS one. The SQL Server paged-read test's remark
  that it "is deliberately not measured in bytes: every read re-reads the
  dataset description from the catalogue" is now stale and can say so.
- The cache is per store instance. Two hosts over one database each hold their
  own, and a read-only host pointed at a database someone else migrates sees the
  change within the window. An operator who needs instantly sets
  `DescriptionCacheTtl` to zero, which is the pre-ADR-0151 behaviour.
- `SqlServerOptions` gains a property and `SqlServerStore` gains an internal
  constructor over a movable clock. No contract interface changes: the TTL is
  store configuration, not a new face.
- `SqlServerTransactionEntry` is no longer a record. Nothing depended on its
  equality.

## References

- ADR-0122 (this record completes it: the PostGIS description cache, and the
  line that deferred SQL Server), ADR-0028 (discovery from the catalogue),
  ADR-0033/ADR-0073 (the store), ADR-0037 (editing), ADR-0041 §3 (ingest),
  ADR-0092 (created datasets carry their indexes), ADR-0111/ADR-0116 (the
  paged-read measurement), ADR-0121 (the per-store collation read this sits
  beside), ADR-0124 (SQL Server pushes the plan's order and page), ADR-0129
  (content version).
- `src/Spatial.Stores.SqlServer/SqlServerDescriptionCache.cs`,
  `src/Spatial.Stores.SqlServer/SqlServerStorage.cs`,
  `src/Spatial.Stores.SqlServer/SqlServerCatalogue.cs`,
  `src/Spatial.Stores.SqlServer/SqlServerOptions.cs`,
  `src/Spatial.Stores.SqlServer/SqlServerStore.cs`,
  `src/Spatial.Stores.SqlServer/SqlServerIngestStore.cs`,
  `src/Spatial.Stores.SqlServer/SqlServerEditStore.cs`,
  `src/Spatial.Stores.SqlServer/SqlServerTransactions.cs`,
  `tests/unit/Spatial.Stores.SqlServer.Tests/SqlServerDescriptionCacheTests.cs`,
  `tests/integration/Spatial.SqlServer.Tests/SqlServerDescriptionCacheTests.cs`.
- Bead SpatialEngine-hj2; bead SpatialEngine-u2x.41 is the PostGIS half.

## Measurements

| What | How | Result |
| --- | --- | --- |
| A walk of 40 rows in pages of 4 | `SqlServerDescriptionCacheTests.A_paged_walk_reads_the_description_once_and_not_once_a_page`, against the container, 2026-10-01 | 10 pages, **1** description read (before: 10, one per page) |
| A description read twice | same class, `A_description_is_discovered_once_and_answered_from_the_cache_thereafter` | 1 catalogue read, 1 held entry |
| Invalidation by each write path | same class, six cases | create, append, edit (add/update/delete), ingest and transaction end all drop the entry |
| The cache as a cache | `Spatial.Stores.SqlServer.Tests.SqlServerDescriptionCacheTests`, 8 cases | hold, drop, per-dataset invalidation, expiry, off, read counting |