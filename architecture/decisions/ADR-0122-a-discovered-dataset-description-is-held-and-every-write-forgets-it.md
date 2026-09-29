---
status: accepted
date: 2026-09-30
deciders: maintainer + agent
---

# ADR-0122: A discovered dataset description is held, and every write the store makes forgets it

## Context

Every read face of the PostGIS provider describes its dataset before it can
compile any SQL. `PostgisPlanReader.ReadAsync`/`CountAsync`/`DistinctAsync`/
`AggregateAsync`, `PostgisFeatures.ScanAsync`/`QueryAsync`/`ByIdentityAsync`,
and the public `IDataCatalogue.DescribeAsync` behind all of them, each call
`PostgisCatalogue.DescribeAsync`, and describing runs five catalogue queries
(`information_schema.columns`, `geometry_columns`, the formatted type
modifiers, the primary-key columns, and the planner's row estimate) to build
one `DatasetDescription`.

So a read cost five catalogue round trips before it cost anything of its own,
and a *walk* of N pages over one layer cost N of them to learn the same schema
N times. ADR-0111 measured it while building paged reads: about 26 MB of
allocation per read, constant whatever the page size, which swamped the
difference between a page and a whole table by two orders of magnitude — so the
paged-read test deliberately did not measure the read in bytes, because the
instrument would have measured the catalogue. It is also a round trip per page
on the wire, on the path a client walks to draw a layer.

A description is a property of a dataset's *shape* — its columns, its identity,
its geometry column and SRID, its declared layout. It is not a property of the
data, except for the row estimate, which is `pg_class.reltuples`: the planner's
own estimate, which no insert updates and only `ANALYZE`/`VACUUM` moves. Every
read of a layer wants the same one, and re-deriving it bought nothing.

The risk is not the hit rate. It is what a *held* description costs when the
table underneath it changes, which is a different question from the one the
bead was filed against.

## Decision

**1. The store holds the description it discovered, keyed by dataset.**
`PostgisDescriptionCache` sits on `PostgisStorage` — the thing that outlives a
call, because the catalogue and feature faces are built per operation — and
`PostgisCatalogue.DescribeAsync` asks it before it opens a connection. One
description read per dataset, per entry. A walk of N pages issues O(1)
description reads, which
`PostgisDescriptionCacheTests.A_paged_walk_reads_the_description_once_and_not_once_a_page`
measures as a count rather than in bytes: the walk is asserted to have read
every row in order first, so a store that answered cheaply by answering nothing
cannot pass it.

The cache is an optimisation, never an answer. Nothing consults it except the
describe path, and a miss is a miss.

**2. Every write the store makes forgets what it could have changed** — create,
ingest (both the batch and the streaming face), append, edit (add, update and
delete alike), and the end of a transaction. Each forgets in a `finally`, so a
write that did not complete cleanly is not a write the store claims to know the
shape of, and a batch that applied some features and failed others still
forgets. A transaction handle records the datasets written through it, so
forgetting at the end is precise rather than a flush of everything.

An append invalidating is the one that looks like overkill — a row does not
change a schema — and it is kept for the field the description also carries:
the row estimate. A client that ingests a layer and then reads its metadata
should not be told the estimate from before the ingest.

**3. A transaction forgets on rollback as well as on commit.** A commit made its
writes visible; a rollback decided they never happened. Either way the decision
is taken *after* any description read, so a description cannot be trusted to
describe the dataset across it. A commit whose outcome is in doubt — one that
threw — is the case where holding a description would be worst, and it forgets
too.

**4. A description that failed to be read is never remembered.** A dataset that
is not a spatial dataset is `not.found` every time it is asked, and a cancelled
read leaves nothing behind. A negative entry would be a claim that a dataset
does not exist, made by a read that could not answer; the ingest that creates
the dataset is exactly the caller that would inherit it.

**5. Out of band, a description expires.** The window is
`PostgisOptions.DescriptionCacheTtl`, 30 seconds by default, and a non-positive
value turns the cache off — which is the store's earlier behaviour, and the
right setting for a database whose schema moves on a schedule this store cannot
see. Expiry is what makes a hand-run `ALTER TABLE`, or a migration run by
another process, a bounded delay rather than a restart. There is no
administrative cache-drop verb: a TTL covers the same ground without a new
public contract face, and the escape hatch an operator reaches for first is a
setting, not an endpoint.

**6. What a stale description may cost, bounded and stated.** Within the window
the store answers with the description it holds, and the consequences are not
symmetric:

- a column **added** out of band is not in the schema, so a plan naming it
  fails `invalid.arguments` — the same refusal a plan naming a column that
  never existed gets, which is a structured failure, not a wrong answer;
- a column **dropped** out of band is still in the schema, so the generated SQL
  names a column the table no longer has and the database refuses it
  (`42703`), surfaced as `store.unavailable` — again a failure, not a wrong
  answer;
- an identity column or a geometry column's **SRID** changed out of band *is* a
  wrong answer, bounded to the window: a read maps values with the old kinds
  and a layer advertises the old `hasZ`/SRID until the entry expires. This is
  the cost the record accepts, and it is why the default is 30 seconds rather
  than "until the store restarts";
- the **row estimate** is stale for the same window, and is an estimate either
  way.

The two cases that fail are the common ones (a migration adds columns), and
they fail as structured errors the caller can act on. The one that is wrong is
narrow, and it is bounded.

**7. The store reports what it did.** `PostgisDescriptionCache.Reads` counts the
descriptions that went to the catalogue, `Count` the ones held, and
`PostgisStore` surfaces both internally. A claim about round trips is only worth
making if it can be measured, and this is the instrument ADR-0111's measurement
needed and did not have.

## Consequences

- A paged walk, a count, a distinct, a grouped reduction and the served layer
  metadata all stop paying per-read catalogue discovery. The same description
  serves the GeoServices and OGC adapters, which describe the layer on every
  request, so the win is not confined to the paged read.
- The cache is per store instance, not shared and not distributed. Two hosts
  over one database each hold their own, which is the honest scope for a cache
  whose invalidation story is the writes *this* store makes.
- A read-only host pointed at a database someone else migrates sees the change
  within the window, not instantly. An operator who needs instantly sets
  `DescriptionCacheTtl` to zero, which is the pre-ADR-0122 behaviour and costs
  what it always cost.
- `DatasetDescription.EstimatedRowCount` is unchanged as a *contract*: it is
  still a planner estimate, and the store still does not promise a count. What
  changed is that the estimate is not taken from before the caller's own write.
- SQL Server has the same shape — `SqlServerCatalogue.DescribeAsync` runs its
  catalogue reads per read — and is deliberately untouched here: this record is
  about one provider, its measurement and its invalidation story. Tracked as its
  own bead.
- The description cache and the collation cache (ADR-0121) are now two
  per-store properties of the database, held in the same place, with the same
  "read once, drop on a failed probe" discipline. A future record that wants a
  third should say whether they belong in one object rather than adding a third
  field.

## Not decided

- **A description cache shared across store instances** (a static, a
  distributed cache, or an `IDistributedCache`): it would let several hosts
  share one discovery, and it would need a cross-process invalidation story
  this record does not have. Not until a deployment shows single-process
  caching to be the limit.
- **An administrative cache-drop verb** on the contract, for an operator who
  wants a change picked up now rather than within the window. The setting is
  the cheaper answer and covers the case; a verb would be a new public surface.
- **Caching the catalogue listing** (`ListAsync`), which still goes to the
  database on every call. It is one query rather than five, and a dataset
  created out of band should appear in a listing quickly.
- **The row estimate itself.** It is `reltuples`, so it is `-1` on a table that
  has never been analysed and stale until an autovacuum analyses it. A store
  that promised a real count would be a different decision about
  `DatasetDescription`.

## Alternatives

- **Cache nothing and describe once per request.** The adapters describe the
  layer and then read through it, so a per-request cache would already have
  taken the paged walk's repeat. Rejected: the paged walk is many requests, and
  the store is a library with no request to hang a cache on.
- **Cache forever, invalidate on write only, document that out-of-band DDL
  needs a restart.** Cheapest and the obvious version. Rejected: it makes an
  `ALTER TABLE` invisible until someone restarts the host, and a store that
  needs a restart to see the database is a store people will not point at a
  migrated database. The TTL is a few lines and no new contract.
- **Validate the entry on every read** — a cheap token (a `pg_class` OID, a
  modification counter) compared against the catalogue before answering. It
  removes the window and keeps the round trip it was introduced to remove: one
  per read instead of five is still O(N) round trips on the walk. Rejected on
  the measurement, which is the whole point of the record.
- **Negative caching** for a dataset that does not exist, to spare a repeated
  `not.found` its five queries. Rejected: an entry is a claim, and a `not.found`
  is the answer a dataset that does not exist *yet* must keep giving — the ingest
  that creates it is the caller that would inherit the wrong one.
- **Invalidate only the datasets a write touched, tracked the way transactions
  track theirs — for every write.** The append and edit paths do forget only
  their own dataset, but they forget in a `finally` on the failure path, where
  the write's own bookkeeping may not have run. Precision about which dataset
  a *successful* write touched is not worth a `finally` that can miss.

## References

- Principles 6 (contracts outlive implementations), 10 (stores are providers),
  12 (persistent state is external), 15 (pushdown optional,
  semantics-preserving), 17 (small kernel).
- ADR-0028 (parameterised SQL, discovery from the catalogue), ADR-0033 (the
  PostGIS store), ADR-0041 §3 (ingest), ADR-0074 §4 (the plan contract),
  ADR-0092 (created datasets carry their indexes), ADR-0098 (the store query
  surface), ADR-0111 (the paged-read measurement), ADR-0116 (a page is a
  position), ADR-0121 (the per-store collation read this sits beside).
- `src/Spatial.Stores.PostGIS/PostgisDescriptionCache.cs`,
  `src/Spatial.Stores.PostGIS/PostgisCatalogue.cs`,
  `src/Spatial.Stores.PostGIS/PostgisStorage.cs`,
  `src/Spatial.Stores.PostGIS/PostgisStore.cs`,
  `src/Spatial.Stores.PostGIS/PostgisIngestStore.cs`,
  `src/Spatial.Stores.PostGIS/PostgisEditStore.cs`,
  `src/Spatial.Stores.PostGIS/PostgisTransactions.cs`,
  `src/Spatial.Stores.PostGIS/PostgisOptions.cs`,
  `src/Spatial.Stores.SqlServer/SqlServerCatalogue.cs`,
  `tests/unit/Spatial.Stores.PostGIS.Tests/PostgisDescriptionCacheTests.cs`,
  `tests/integration/Spatial.PostGIS.Tests/PostgisDescriptionCacheTests.cs`,
  `tests/integration/Spatial.PostGIS.Tests/PostgisPagedReadTests.cs`.
