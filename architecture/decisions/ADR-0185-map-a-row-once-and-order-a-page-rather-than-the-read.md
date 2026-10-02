---
status: accepted
date: 2026-10-07
deciders: maintainer + agent
summary: **A read that drops nothing maps each row once, and a capped plan is ordered only as far as its page reaches.** Two costs sat on the answer a keyless layer's plan read is given — a whole read finished by the reference executor (ADR-0097 §1, ADR-0184 §2), measured at 46.7 MB against 31.1 MB for a plain scan of the same 34,135 rows — and neither was the one ADR-0184 filed it as. The 15 MB residual was **not** `FeaturePlanExecutor.Select`: `Select` copies references (0.26 MB over the whole layer). It was the *stores'* row mapping — `Shape.Project` in both SQL plan readers built a second `AttributeValue[]` and a second `Feature` for **every row read**, even when the read had appended no identity column to drop, because the result schema was always a freshly built `FeatureSchema` and the `ReferenceEquals(read, Result)` guard could never hold. Decided once per read instead, in `FeatureRowProjection` (shared by both stores, not store-local), a projection that drops nothing returns the row's own feature. The executor's half is the order: a capped plan now takes a **bounded selection** over the window `offset + limit` rather than sorting every row, with the read's own position as the last tie-break so the order is total and the window of the bounded selection is the window of the full sort.
amends: ADR-0184
related: ADR-0074, ADR-0097, ADR-0116, ADR-0127, ADR-0131
---

# ADR-0185: Map a row once, and order a page rather than the read

## Context

A dataset that declares no identity column names its features by the ordinal of
the read, so no pushed `WHERE` is admissible and its plan read is a whole read
finished in managed code by the reference executor (ADR-0097 §1, ADR-0184 §2).
That answer is correct. What the baseline spike measured is that it was
**expensive out of proportion to the page it returns**: a capped read (order plus
`LIMIT 25`) of the 34,135-row keyless world-cities layer allocated 46.7 MB
against 31.1 MB for a plain full scan of the same table, and the figure did not
move with the page size — 25 rows and 6,253 rows cost the same. So a caller who
asked for less work paid more than one who asked for everything, which is the
shape an operator would not expect. ADR-0184 §Not decided filed the residual
against `FeaturePlanExecutor` on the strength of a stage bisection that had put
the whole figure in the fallback read.

Measured here, over the same shape at 34,135 rows (`FeaturePlanExecutor` over a
resident layer, and a live PostGIS table read through `PostgisStore`):

| stage | allocation | share of the residual |
| --- | --- | --- |
| `FeaturePlanExecutor.Select` over the whole read | 0.26 MB | 0.1 MB |
| `FeaturePlanExecutor.Finish`, order + limit 25 | 4.0 MB | 4.0 MB |
| the fallback read's row mapping, per row, whole read | — | the remaining ~11 MB |

`Select` does not materialise a feature per row — it copies references into a
list — so the executor was never the second materialisation ADR-0184 named. The
store's mapping was, twice over, in two copies of one loop: `Shape.Project` in
`PostgisPlanReader` and in `SqlServerPlanReader`, each building an
`AttributeValue[]` and a `Feature` for **every row** after the row mapper had
already built one. It tried not to, behind `ReferenceEquals(read, Result)`, but
`Columns` always constructs a fresh `FeatureSchema` for the result even when it
holds the identical fields, so the guard could never be true and the per-field
`read.IndexOf(...)` ran on every field of every row as well. A whole read of a
keyless layer — and a whole-table reduction that falls back to one — is exactly
where that loop runs over every row.

The other half is the order. `Finish` sorted the whole selected set to name the
page's first `offset + limit` rows, allocating a key and an index per row per
key: 4.0 MB at 34,135 rows, for a 25-row page.

## Decision

**Decide a read's row projection once per read, and select a capped plan's
window rather than sorting the whole read.**

- `FeatureRowProjection` (`Spatial.Querying`, shared by both SQL stores) holds a
  read schema and a result schema. When they are the same fields in the same
  order — which is the case whenever the read appended no identity column — it
  holds no index map and `Apply` returns the row's own feature. When they are
  not, it holds the index map computed once and `Apply` builds the result
  feature. A result field the read does not carry is refused when the projection
  is built, once, rather than per row.
- Both `Shape` records hold a `FeatureRowProjection` in place of the per-row
  `Project` method, and their `Result` is the projection's. The two copies of
  that loop are now one definition.
- `FeaturePlanExecutor.Finish` computes `start` before ordering and orders only
  the window `start + limit`. With no cap, or a window that covers the read, the
  order is the whole read's sort; below that it is a bounded selection — a
  max-heap of the window over the read, so the rows behind the page are compared
  and dropped rather than held. The total order gains a last key, the read's own
  position, which is what makes the order total even when a read names two
  features alike; with the order total, the window of the bounded selection and
  the window of the full sort are one answer.
- `Page` takes the read's total rather than the ordered list's count, so
  exhaustion and the continuation cursor are decided against the whole read and
  never against the window.

## Alternatives

- **Fix it in each store's `Shape.Project` alone** — a `ReferenceEquals` guard
  that works, in two places. Rejected: the same defect in two copies is one
  defect, and a store-local fix is a third copy to remember. The shared type is
  where both stores already meet for the plan's semantics (ADR-0074 §4's
  reference evaluator is shared the same way).
- **Return the same feature from `Finish` for the whole read and sort nothing.**
  Rejected: it is what the code did, and the sort is where the plan's order is
  decided. The cost was the sort of rows the page never names, not the sort.
- **Give `Finish` the page's window and drop the rows behind it as it scans.**
  That is what the bounded selection does; the alternative considered was a
  single pass keeping only the page's `limit` rows, which cannot work — a row
  that is not in the page's window can still displace one that is, and the
  window is the page start, not the page size.
- **Push the cap into the fallback read.** Rejected and unchanged: it is the
  whole-table read that ADR-0097 §1 and ADR-0184 §2 make the answer, and this
  record does not reopen them.
- **A keyless layer's plan read is a scan and a filter, so give the store the
  count of the match set instead of the rows.** That is ADR-0184 §1's reduction
  rule, already decided, and it does not answer a feature page.

## Not decided

- **Keying a keyless layer.** Still ADR-0184's first open bullet, and untouched
  here: the way out of the whole read is an identity, and the costs are the ones
  that record already weighed.
- **A page at a deep offset costs its window, not its page.** At offset 33,000
  the bounded selection holds 33,025 candidates, so a deep page of a walk is
  cheaper than a sorted read and dearer than a page at the start. Getting that
  to the page's `limit` needs a partitioning selection rather than a heap, and
  nothing measured here needs it.

## Consequences

- **A capped read of a keyless layer costs what the scan it had to do costs.**
  On the 5,000-row keyless table the plan read fell from 1.65× the scan to
  1.02×, and a whole-table count from 1.53× to 1.01×; the figure that remains
  is the page's projection and the executor's window.
- **Both SQL stores are fixed by one change**, and a store that maps rows the
  same way has one helper to reach for rather than two loops to copy.
- **The executor's order is bounded by the page's window.** A plan over a whole
  read that returns everything is unchanged: with no cap the order is the whole
  read's sort, at the same cost as before.
- **The order gained a tie-break.** Two rows of one identity in one read are now
  separated by the read's own order rather than by the sort's stability. Nothing
  in the contract or the conformance suite distinguishes them — the identity is
  the same string for both — and a store that cannot separate two rows by their
  identity could not make a pushed `ORDER BY` total either (ADR-0131).
- **`Finish` no longer copies the selected list before ordering it.** With no
  order the list is used as it is; `Page` copies the range it returns, so the
  caller's list is never handed out.
- **The cost of this record is a second ordering implementation.** The full sort
  and the bounded selection are two pieces of code that must agree, which is why
  `A_window_of_the_bounded_order_is_the_window_of_the_full_order` states the
  window against an oracle written apart from both.

## References

- ADR-0074 §4 and §6 (the reference executor is the definition; a store that
  cannot push finishes what it read), ADR-0097 §1 (the keyless decline),
  ADR-0116 §1 (a keyless page is the reference's page), ADR-0127 (composite
  orders are then-keys), ADR-0131 (a pushed read names a row by the key it
  read), ADR-0184 §2 and §Not decided (the bullet this record resolves).
- `src/Spatial.Querying/FeatureRowProjection.cs` (new),
  `src/Spatial.Querying/FeaturePlanExecutor.cs` (`Finish`, `Order`, `Page`),
  `src/Spatial.Stores.PostGIS/PostgisPlanReader.cs` (`Columns`, `Shape`),
  `src/Spatial.Stores.SqlServer/SqlServerPlanReader.cs` (the same two).
- `tests/unit/Spatial.Stores.Memory.Tests/FeaturePlanExecutorAllocationTests.cs`,
  `tests/integration/Spatial.PostGIS.Tests/PostgisKeylessPlanReadAllocationTests.cs`.
- Beads: SpatialEngine-yup, answering ADR-0184's second open bullet;
  SpatialEngine-d20 recorded the measurement, SpatialEngine-xg5 wrote ADR-0184.
- `eng/spike-u2x-query-baseline/RESULTS.md` §The PostGIS `rows` column is not
  what the database read.

## Measurements

| What | How | Result | Date |
| --- | --- | --- | --- |
| The cost being removed | `GC.GetTotalAllocatedBytes(precise: true)` around `FeaturePlanExecutor.Select` and `Finish` over a resident 34,135-row layer, ordered by a repeated key with `LIMIT 25` | `Select` 0.26 MB, `Finish` 4.0 MB, the same plan unordered 0.52 MB | 2026-10-07 |
| The residual is the store's mapping, not the executor | the same bisection read against `PostgisStore` over a 5,000-row keyless table in the container | the plan read allocated 1.65× the scan and a whole-table count 1.53×, against a `Select` measured at 0.1 MB of the figure | 2026-10-07 |
| The pushdown still does not happen | `PostgisKeylessPlanReadAllocationTests` — the page is 25 rows, the total is the whole table, and the table declares no identity column | pinned | 2026-10-07 |
| The window is the full order | `FeaturePlanExecutorAllocationTests.A_window_of_the_bounded_order_is_the_window_of_the_full_order` — seven windows of a two-key order with 1,000 ties, against an oracle written in the test | the same features, window for window | 2026-10-07 |
| The cost after | the same 5,000-row keyless container table | plan read 1.02× the scan (was 1.65×), whole-table count 1.01× (was 1.53×) | 2026-10-07 |
| The executor after | the 34,135-row resident layer, ordered, uncapped | 1,483 KB for the whole read's order; 270 KB for a 25-row page at offset 0, 282 KB at offset 1,000, 658 KB at offset 33,000 | 2026-10-07 |
