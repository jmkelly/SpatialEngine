---
status: accepted
date: 2026-09-29
deciders: maintainer + agent
summary: A paged read is a page, a position and a “one more” — never a materialised match set — and a store reads the page whenever the page has a position.
---

# ADR-0116: A paged read is a page, a position and a "one more" — never a materialised match set

## Context

ADR-0074 made the feature read a plan and ADR-0098 pushed that plan onto the
stores, so a store can express the restriction, the order, the cap and the
continuation in its own dialect. What was left of the original unbounded read
was the shape of the answer, and it was still unbounded in the two places that
matter on a large layer.

**The store read face.** `PostgisPlanReader.ReadAsync` pushed the page only
when it had pushed a `WHERE`. A plan that restricted *nothing* — no `where`, no
box — fell into the reference branch, which reads the whole table and then
pages in memory:

```csharp
if (where is null || (query.Order is { Count: > 0 } && order is null))
{
    return FeaturePlanExecutor.Finish(description.Schema, await SelectedAsync(...), query, cancellationToken);
}
```

A `where=1=1` request is restricted (`Predicate.Constant(true)` compiles to a
SQL `(1 = 1)`), so the REST JS `queryAllFeatures` loop was paged. A request with
no `where` at all — which every client sends eventually, and which
`orderByFields` makes pageable — was not: a bbox-free, where-free ordered
query over a 200k-row table built all 200k features on the way to a 1000-row
response. The branch conflated two different reasons to fall back, and only one
of them is a real one.

**The served surface.** `StoreQueryPath.FeaturesAsync` read the store's page
and then *re-minted* the continuation:

```csharp
var offset = Math.Min(FeaturePaging.ResolveOffset(query), int.MaxValue);
var next = offset + features.Length;
… page.NextCursor is not null ? ResultPagination.Encode(next) : null
```

So the `resultPaginationToken` a client replayed was an adapter-minted offset
into a list the adapter had already thrown away, and the plan carried it as
`Offset` — the position was never the store's. The paging was *logically*
correct (the T-015 replay terminated with the exact total in stable order) and
that correctness was exactly the problem: nothing in the token said where the
rows were, so nothing kept the surface honest about not holding them.

**The "is there more" signal.** A page could be continued by
`NextCursor is not null`, so exhaustion and "this store did not compute a
continuation" were the same value. A store that could tell from the read itself
— it fetched the cap and looked for one row past it — had no way to say so
without inventing a token.

## Decision

**1. A store reads the page whenever the page has a position.** A plan is read
with a `LIMIT` and an `OFFSET` when either the dialect expressed its
restriction, or the plan asked for an order the table can make total (the
identity tie-break included). An empty restriction is a read of the whole
table, and a whole table read with a `LIMIT` is a page. Only two plans still
fall back to the reference over a whole read, and both are the same reason: the
plan's order is not a total order this dialect can reproduce, so a row's place
in a page is not a position the next statement can name:

- the plan asked for no order (its order is the scan order), and
- the plan asked for an order the table cannot make total (no identity column,
  or a key the dialect cannot sort).

`PostgisPlanReader.Pushed` is that decision, and it is the seam a store without
a pushdown does not have.

**2. A page says whether more remains.** `FeatureQueryPage` carries
`HasMore` beside `NextCursor`, and `FeatureQueryPage.Page` is the one place the
invariant is written down: a page that says more always carries the cursor that
reaches it, a page that does not carries none. The reference executor knows it
from the page it just cut; a pushed read knows it from the count the plan's
`SELECT COUNT(*)` already answered. No caller infers exhaustion from the shape
of a token any more, and the Esri `exceededTransferLimit` flag is the store's
own answer rather than a re-derivation.

**3. The `resultPaginationToken` is the store's continuation.** A request the
store answers hands the token to the plan as its `Cursor` and serves the
store's `NextCursor` back verbatim. The store validates it — the cursor
carries a fingerprint of the plan it was issued for — so a token replayed with
a different cap, order, projection or restriction is `invalid.arguments` and the
client restarts from the first page, instead of silently paging a different
question. `resultOffset` is unaffected: an offset is an offset, and a client
that never sends a token is not pushed onto one.

**4. A store with no pushdown is told what it owes, and what it does not.** The
in-memory provider (and any store whose dialect cannot express the plan) pages
through the same reference executor: its *answer* is one page whatever the
layer's size, it states whether more remains, and it refuses a cursor it did
not issue. What it cannot promise is reading fewer rows than it holds — the
dataset *is* that provider — and the contract now says so in words instead of
leaving the two claims to be confused. Paging a large layer off the heap needs
a store that can push the page; on a store that cannot, paging still keeps the
answer small.

## Consequences

- A where-free, ordered query over a large layer is a `LIMIT`/`OFFSET` read.
  The SQL shape is pinned in `PostgisPlanSurfaceTests`/`PostgisPlanPagingTests`
  and the walk is pinned against a real database in `PostgisPagedReadTests`.
- The Esri `resultPaginationToken` changes shape for a pushed read: it is the
  store's opaque cursor (fingerprint-bound) rather than a base64 offset. Both
  are opaque to a client, which is what §9.1.4 asks for, and the token is still
  only valid for the query that minted it — now enforced by the store rather
  than by the adapter's convention. A bogus token is therefore the *store's*
  typed `invalid.arguments` in process, where it was the adapter's
  `EsriInteropException`; over the wire nothing changes, because the codec maps
  a `SpatialException` to the same Esri code, status and message
  (`EsriJson.Describe`), which is how every other store failure on this path
  has always been reported.
- The match path (a layer whose `OBJECTID` is the scan ordinal, a `time`
  filter, `objectIds`, a topological relation) keeps its own token: there the
  surface *is* the thing that evaluated the match, so the position really is an
  index into a match set it holds. The two vocabularies never meet on one
  request, which is what makes a crossed token a typed failure.
- A store's total is now counted on every pushed read, `WHERE` or not. An
  aggregate row is not a row set, and it is what lets a page say whether more
  remains without over-fetching a row past the cap.

## Not decided

- The SQL Server store still pushes the restriction and finishes the plan over
  the rows it selected, for the reason its own comment gives: reproducing the
  reference's null ordering and collation from T-SQL `ORDER BY` needs its own
  argument. It is a follow-up, not a claim of this record.
- Every read re-reads the dataset description from the catalogue, which is a
  fixed cost per page and two orders of magnitude larger than the difference
  between a page and a whole table. It is what makes a byte-level measurement
  at the store level meaningless, and it is a follow-up bead.
- A keyset cursor (a position in the *order*, not an offset into it) is a
  stronger position than an offset and would make a page stable across writes
  to the layer. The contract's cursor is opaque, so this is a store-internal
  improvement, not a contract change.

## Alternatives

- **Read `Limit + 1` rows and drop the extra instead of counting.** Cheaper in
  rows, and the honest "one more" signal. Rejected for the pushed path because
  the conformance suite compares the page's `TotalCount` with the reference's
  for a capped plan, and the count is the number `returnCountOnly` reports;
  a store that over-fetches throws it away. A store that wants the over-fetch
  can set `TotalCount` to `null` and say so.
- **Keep minting the adapter's token and keep the plan's `Offset`.** No
  contract change at all. Rejected: it is the arrangement that made the paging
  logically correct and materially unbounded, and a token that can be replayed
  against a store that never saw it is a token that cannot be used to prove
  where the rows were.
- **Give `FeatureQuery.Limit` a default page size, so an uncapped plan is a
  page.** Tempting, and it would make "the read is a page" true by
  construction. Rejected: `FeatureQuery.All` is the unbounded read by
  definition (ADR-0074) and `ScanAsync` is that plan by another name, so a
  default cap would silently change the meaning of every plan that says nothing
  about its page. The engine's canonical page size is documented on `Limit` and
  is what a caller asks for; the surfaces that page raise it to their own cap.
- **Make the read an `IAsyncEnumerable<FeatureBatch>`.** Rejected in ADR-0074
  and nothing here changes it: no read in the engine needs a stream, and a
  cursor plus a "one more" is the whole of what a request/response protocol
  can use.

## References

- Principles 6 (contracts outlive implementations), 10 (stores are providers),
  15 (pushdown optional, semantics-preserving), 17 (small kernel).
- ADR-0023/ADR-0029 (batch pages), ADR-0033 (in-process interfaces, optional
  additive faces), ADR-0037 (the synthetic `OBJECTID` and its durable
  counterpart), ADR-0040 (provider code organisation), ADR-0056 (Feature query
  params are an adapter projection), ADR-0074 (the plan contract, §5 paging and
  the cursor), ADR-0092 (PostGIS spatial indexes), ADR-0097/ADR-0098 (the
  pushdown surface and the served query compiled onto it).
- `src/Spatial.Core/Features/Query/FeatureQueryPage.cs`,
  `src/Spatial.Querying/FeaturePlanExecutor.cs`,
  `src/Spatial.Stores.PostGIS/PostgisPlanReader.cs`,
  `src/Spatial.Adapter.GeoServices/StoreQueryPath.cs`,
  `src/Spatial.Adapter.GeoServices/ResultPagination.cs`,
  `tests/unit/Spatial.Adapter.GeoServices.Tests/PagedStoreReadTests.cs`,
  `tests/unit/Spatial.Stores.PostGIS.Tests/PostgisPlanPagingTests.cs`,
  `tests/integration/Spatial.PostGIS.Tests/PostgisPagedReadTests.cs`,
  `tests/unit/Spatial.Stores.Memory.Tests/MemoryPagedReadTests.cs`.
