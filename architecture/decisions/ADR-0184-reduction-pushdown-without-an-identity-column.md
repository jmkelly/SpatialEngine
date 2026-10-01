---
status: accepted
date: 2026-10-06
deciders: maintainer + agent
summary: **A reduction pushes its restriction on a dataset that declares no identity column; a feature read still does not.** The ADR-0097 §1 decline is about *features* — a `WHERE` that returns only the matching rows renumbers a dataset whose features are named by the ordinal of the read — and a count, a distinct set and a grouped reduction return values and no feature, so there is nothing to renumber. `PostgisPlanQueries.Reduction` is that restriction, the same compiler and the same bound values as the read's, with the keyless decline lifted and one exception: an identity restriction a dataset cannot state (`Ids` on a dataset with no identity columns) still keeps the whole plan in the caller, because a half-pushed restriction is not a smaller read but a different answer. The feature read on such a dataset stays a whole read, page and all, because the ordinals a page's rows are named by are the ordinals of that page (amends 0097).
amends: ADR-0097
amended-by: ADR-0185
related: ADR-0116, ADR-0131, ADR-0133, ADR-0140, ADR-0147, ADR-0149
---

# ADR-0184: A reduction pushes its restriction on a dataset with no identity column

## Context

`IDataCatalogue.CreateAsync` creates a table from the defining batch's schema
and adds no primary key: a creating schema is the client's own and may declare
no integer identity field. ADR-0147 made that deliberate — a created dataset
keeps the **keyless** shape ADR-0131's fixture and ADR-0140's refusal are
written against, and a created table is shaped like nothing an ingest builds —
and ADR-0149 §3 pulled the other half of the lever deliberately the other way,
so ingest keys and create does not. A created PostGIS dataset therefore
declares no identity column, its features are named by the ordinal of the read
(ADR-0037), and ADR-0097 §1 stops the store from pushing a `WHERE` onto it.

That rule is right about a *feature read* and was being applied to faces it does
not describe. `PostgisPlanReader` compiled every plan's restriction through one
method, so a count, a distinct set and a grouped reduction over a keyless layer
read the whole table and reduced it in managed code — and none of the three
returns a feature. ADR-0116 already said why a count exists at all: "an
aggregate row is not a row set", and the count is what lets a page say whether
the plan has more. The store was computing, in managed code, numbers the database
answers from the same rows it would have counted anyway.

The cost was measured while answering SpatialEngine-d20 (`eng/spike-u2x-query-
baseline/RESULTS.md`, 2026-10-05): on a 34k-row keyless layer, `returnCountOnly`
and `outStatistics` cost what a full scan costs, while the same
`SELECT COUNT(*)` through raw Npgsql allocates three orders of magnitude less.
The whole of it was in the fallback read.

## Decision

**Push a reduction's restriction on every dataset, and leave the feature read's
decline where it is.**

**1. `PostgisPlanQueries.Reduction` is the read's restriction with the keyless
decline lifted.** Same three halves, same compiler, same bound values, same
catalog read for the collation (ADR-0121, ADR-0123) — so a plan cannot be
stated two ways by two faces. The whole of the difference is ADR-0097 §1 read
for what it is about: *which features a read returns and what they are named*.
A count answers with a number, a distinct set with values, a grouped reduction
with group rows; none of them returns a feature, so none of them can renumber
one. The pushed statement is therefore the reference's own answer, computed by
the database instead of by the engine — which is what ADR-0074 §4 and
principle 15 already ask of every other pushdown.

**2. One restriction is still declined: an identity restriction the dataset
cannot state.** `Ids` name features by their identity columns, and a dataset
that declares none has no expression to say them with. A reduction that pushed
the *rest* of such a plan — the box, the attribute clause — would be counting a
different row set than the plan named, so the whole restriction stays in the
caller and the reduction is finished over the selected rows. A half-pushed
restriction is not a smaller read; it is a different answer. An **empty** id
list is the one case that needs no identity to state: it is `FALSE`, so the
count of it is zero.

**3. The feature read on such a dataset is unchanged, page and all.** A page is
addressed only when the plan's order is a total order the table can reproduce
(ADR-0116 §1), and a keyless table has no identity to tie-break on. The deeper
reason is the one the store already states about its own ids: the features a
keyless read returns are named by the ordinal of *that read*, so a page cut from
a `LIMIT`/`OFFSET` would name its first row `1` on every page. There is no
`ORDER BY` that fixes this, because the number is not a property of the row. So
the read stays a whole read, and that is the price of ADR-0149 §3 — a price
worth stating rather than re-discovering per call.

**4. The reason is stated where the decisions are taken.** The decline is
pinned in `PostgisKeylessReductionTests` beside the pushdown it now qualifies,
and the read's own decline stays pinned in `PostgisPlanPagingTests`, so a change
to either half is a test failure and not a performance regression nobody
notices. There is no operator-facing surface to add: the providers hold no
logger, and `DatasetDescription` is served as contract vocabulary, so a note in
the description would be a contract field about a provider's internals. The
measurement harness already prints the note.

## Alternatives

- **Give a created PostGIS dataset an identity column** (a `bigserial`
  `PRIMARY KEY`, or a key over the schema's own integer field). This is the
  option that would also fix §3, and it was rejected here for the reason
  ADR-0149 §3 gives: keying `CreateAsync` would undo ADR-0147 for one path and
  not the other, so a `CreateAsync` sample batch and an ingest of the same
  document would describe differently under one contract — and it would make
  every created layer editable, lookup-able and renumberable as a side effect
  (ADR-0037, ADR-0140), which is a change to the served surface and not a
  performance fix. It deserves its own record and its own bead, not a column
  added inside a performance one.
- **Push the whole-table read for a keyless layer with a `LIMIT`.** The page is
  the win ADR-0116 §1 took, and the reason it does not apply here is §3: the
  rows of page two would be numbered from one again, so the same feature would
  carry two ids and `objectIds`, `returnIdsOnly` and the edit round-trip would
  break on a plan that is otherwise right.
- **Order by a physical row id (`ctid`) to make the order total.** `ctid` is
  unique and needs no column, so it looks like the free total order §3 asks
  for. Rejected: it is not durable — Postgres assigns a new one on every update
  — so a page boundary computed before a concurrent write names a different
  position after it, and a walk can skip or repeat a row. A cursor that is not
  stable across a write is not a position (ADR-0116 §3).
- **Push the residual `WHERE` and let the facade renumber** (ADR-0097 §1's
  alternative reading). Rejected on the record that wrote the rule: `objectIds`,
  `returnIdsOnly`, `resultOffset` paging and the edit round-trip all treat the
  scan ordinal as a key, and the Esri docs replay fixtures caught it
  (`query-where` expected `[2,3,4,6,7]` and got `[1,2,3,4,5]`).
- **Measure the win with `GC.GetTotalAllocatedBytes` in the test suite.** The
  figures are real and belong in this record, but process-wide allocation
  counters are not a gate: under parallel test classes the delta is noise plus
  signal. Replaced by a fact about the table (§ Measurements).

## Not decided

- **A keyless layer's plan read, other than by keying it.** The whole read stays;
  the way out is an identity, and §1 above says what that costs. A keyed
  `CreateAsync` would reopen ADR-0147 §2 and ADR-0149 §3 together, for both
  providers, and should be argued as one decision.
- ~~**The residual selection's allocation on that read.**~~ **Decided by
  ADR-0185**, and the attribution above was wrong: the whole read stays, but
  the cost was not `FeaturePlanExecutor`'s residual. `Select` copies
  references; the 15 MB was both SQL stores' row mapping building a second
  feature per row, and the executor's full sort on top of it. Both are
  removed, one definition for the mapping and one bounded selection for the
  order, and a capped read of a keyless layer now costs what the scan it had to
  do costs.

## Consequences

- **`returnCountOnly`, `returnDistinctValues` and `outStatistics` on a created
  PostGIS layer stop reading the layer.** A count is a `SELECT COUNT(*)`, a
  grouped reduction a `GROUP BY`, a distinct set a `SELECT DISTINCT` — the
  shapes the store already wrote and already pinned for keyed datasets. The
  Esri statistics, extent (ADR-0120) and distinct paths inherit it, since they
  are compiled onto the same faces.
- **The fallback is still there and still correct.** A reduction whose
  `Ids` cannot be stated, and a distinct set whose order the dialect cannot make
  total over the distinct rows (ADR-0133 §6), are reduced in managed code over
  the rows the pushed restriction selected — which, on a keyless dataset, is now
  the restricted set rather than the table.
- **No contract type changed and no dataset changed shape.** `IDataFields`,
  `FeatureQuery` and `DatasetDescription` are untouched; a created dataset still
  describes the same columns with the same empty `IdColumns`, and every
  conformance fixture that holds a keyless dataset still passes because the
  answers are the reference's.
- **A dataset with an identity column is unaffected.** The two restrictions are
  the same expression there, and
  `A_reduction_on_an_identity_carrying_dataset_pushes_exactly_what_the_read_pushes`
  pins that they cannot drift.
- **The cost.** A keyless layer's *feature* read is still a whole read, and this
  record does not change that, so the Esri feature path on such a layer is
  unchanged: it is the scan-and-match path the adapter already takes for a layer
  whose `OBJECTID` is an ordinal (`StoreQueryPath`).

## References

- ADR-0037 (the synthetic `OBJECTID` is the scan ordinal), ADR-0074 §4 (a
  pushdown is result-preserving, per-conjunct and best-effort), ADR-0097 §1–2
  (the decline this record scopes to features; literal binding, unchanged),
  ADR-0116 §1 and §3 (the page, the position and the "one more"),
  ADR-0120 (the extent is a reduction), ADR-0128 (the group page),
  ADR-0131 (a pushed read names a row by the key it read), ADR-0133 §6 (a
  `DISTINCT` that cannot state its order is reduced in managed code),
  ADR-0140 (a dataset with no identity column has no durable key),
  ADR-0147 (a created dataset stays keyless in the contract's view),
  ADR-0149 §3 (ingest keys, create does not, on purpose).
- `src/Spatial.Stores.PostGIS/Core/PostgisPlanQueries.cs` (`Predicate`,
  `Reduction`), `src/Spatial.Stores.PostGIS/PostgisPlanReader.cs`
  (`RestrictionAsync`, `ReductionAsync`, `CountAsync`, `AggregateAsync`,
  `DistinctAsync`), `src/Spatial.Stores.PostGIS/AGENTS.md`.
- `tests/unit/Spatial.Stores.PostGIS.Tests/PostgisKeylessReductionTests.cs`,
  `tests/unit/Spatial.Stores.PostGIS.Tests/PostgisPlanPagingTests.cs`,
  `tests/integration/Spatial.PostGIS.Tests/PostgisKeylessReductionPushdownTests.cs`.
- Beads: SpatialEngine-xg5 (this record), answering SpatialEngine-d20; the
  ingest half is SpatialEngine-2cm / ADR-0149.

## Measurements

| What | How | Result | Date |
| --- | --- | --- | --- |
| The cost being removed | `eng/spike-u2x-query-baseline.sh --store=postgis --iterations=10 --warmup=3` on the 34k-row keyless world-cities layer, with a `GC.GetTotalAllocatedBytes` bisection around each stage of `PostgisStore.QueryAsync` | `returnCountOnly` and `outStatistics` cost what a full scan costs (31.1 MB); the same `SELECT COUNT(*)` through raw Npgsql allocates 0.01 MB | 2026-10-05 |
| The pushdown happens | `PostgisKeylessReductionTests` (unit) — the restriction, the count SQL, the declined `Ids`, the empty id list, and the read's unchanged decline | pinned | 2026-10-06 |
| The pushdown happens, against a live PostGIS | `PostgisKeylessReductionPushdownTests` — a 5,000-row table with no primary key, counted and grouped through the store | the count, the four groups and the distinct set are the reference reduction's, value for value | 2026-10-06 |
| The table is not read | the same fixture, seeded so one row holds a `CIRCULARSTRING` — a geometry the EWKB reader refuses. A scan of that table fails with `store.unavailable`; a count and a grouped reduction over it are answered, because neither decodes a geometry. Before this record the count failed the same way the scan does | 10 rows counted, 4 groups | 2026-10-06 |
| A counter that does not work | `pg_stat_database.tup_returned` was tried first as the row witness and abandoned: the pooled backends report their counters on their own schedule, so a whole read of 20,000 rows measured 5,186 | discarded | 2026-10-06 |
