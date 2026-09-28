---
status: accepted
date: 2026-09-28
deciders: maintainer + agent
---

# ADR-0084: The store query surface — projection, order, paging, count, distinct and aggregate

## Context

ADR-0074 decided the *shape* of a feature-read: a core-typed plan with a
predicate, and it left two things open. §5 said paging and ordering go on the
plan, that a read answers a `FeatureQueryPage(Batches, NextCursor, TotalCount?)`
and that a store must append the feature identity as a tie-break to any
requested order. §6 said everything that asks a store to *reduce* is additive,
on an interface, and ended with: "**Not decided:** … the aggregate-spec shape
`IFeatureAggregateStore` takes, which `.9` settles against the Esri statistics
surface (ADR-0057)."

What the adapter served over that contract was a projection over a fully
materialised result: `outFields` per feature, `orderByFields` as a stable sort
of every match, `outStatistics` grouped and reduced in
`FeatureStatisticsEngine`, `resultOffset`/`resultRecordCount` applied after the
fact in `FeaturePaging`, `returnCountOnly` and `returnDistinctValues` counted
and deduplicated in `FeatureResponseWriter` — every one of them over a
`ScanAsync` that read the whole dataset. `ReadBatchesAsync` in the PostGIS
provider materialises the entire match set before it chunks it, so a `LIMIT` a
caller could have asked for was never asked for.

## Decision

**1. The plan carries the shaping; the read answers a page.**
`FeatureQuery` gains `Projection`, `Order` (`IReadOnlyList<OrderTerm>`),
`Limit`, `Offset` and `Cursor`; `IFeatureStore.QueryAsync` returns
`FeatureQueryPage(Batches, NextCursor, TotalCount?)`. `TotalCount` is nullable
and `null` means *not computed*, never zero (ADR-0074 §5), so a page is a page
and a count is an optimisation of a read rather than a capability.

**2. Reductions are additive: `IFeatureAggregateStore`.**
`CountAsync`, `DistinctAsync` and `AggregateAsync`, each taking the plan that
*selects* and the request that *reduces*, and each a cancellable task. The
aggregate spec is the Esri statistics surface's own vocabulary —
`AggregateStatistic` = count, sum, min, max, avg, variance, stddev,
`percentile_cont`, `percentile_disc` — with `Field` (`*` for a row count),
`ResultName`, `Percentile` and `PercentileDescending`. No new statistic is
invented and none is dropped, because the surface already serves all nine and
ADR-0057 already forbids answering a statistic the store cannot honour with a
different number.

**3. The semantics are stated once, and a pushdown is measured against them.**
A new `Spatial.Querying` project holds the reference executor, the value
ordering, the page-cursor codec and the caller-side reduction. It is what
"correct" means, in one place:

- **Ordering.** Nulls sort last ascending (first descending); strings compare
  ordinally, never by a locale collation; a store appends the feature identity
  as a final ascending key, so the total order is deterministic and a page
  boundary can never fall between two rows the next page would re-order.
- **Paging.** `Cursor` and `Offset` are alternatives, never composed. A cursor
  is store-issued and carries a fingerprint of the plan it was issued for, so a
  token that is malformed, or that belongs to a different restriction, order,
  projection or cap, is `invalid.arguments` rather than a different page.
- **Projection.** The reduced schema is what the page's batches carry, in the
  requested order. A feature's identity is never projected away.
- **Nulls in reductions.** Nulls are skipped, not counted as zero: a count is
  the number of non-null values, a sum ignores nulls, and a statistic with no
  non-null input is a **null**, never a zero. An integer field's sum is an
  integer; every other numeric reduction is a double; variance and standard
  deviation are the sample forms.
- **An empty set.** An ungrouped reduction of an empty set is **one group of
  nulls**, not zero groups — the one row an empty statistics query answers
  with. A grouped reduction of an empty set is no groups. A count is zero.
- **Order of a reduction.** A distinct row set and a group order are *the
  order the plan asked for*. A plan that asks for no order takes the store's
  own order — which SQL cannot reproduce, so a pushdown of a reduction is
  offered only when the plan requests an order, and the reader finishes in
  memory otherwise. Never a different order, never a silent degradation.

**4. A store that cannot push down still answers correctly.**
`FeaturePlanFallback` (read the rows, evaluate the plan with the reference) and
`FeatureReductionFallback` (use `IFeatureAggregateStore` when the store
implements it, else reduce over what the store answered — ADR-0074 §6's rule,
with the interface as the only capability probe, so there is no flag to lie).
Memory, Demo and ArcGIS REST use the fallback directly; SQL Server does too,
and its own T-SQL pushdown is a follow-up that has to reproduce the reference
exactly. PostGIS pushes the plan read (projection, order with explicit null
placement, `LIMIT`/`OFFSET`), `COUNT(*)` and the grouped aggregate, and
finishes the three cases §3 names above in memory.

**5. The reference lives in one shared project, not per store.**
`Spatial.Querying` references Core and the SDK, takes no packages and holds no
provider type. The alternative — a copy of the executor in each store, as
ADR-0074's predicate evaluator ended up — makes "pushdown equals reference" a
comparison between two implementations, which is how the two provider filter
grammars came to exist in the first place. The architecture guard's
implementation-project rule gains `Spatial.Querying` as a named, ADR-backed
allowance rather than a widening.

**6. The filter-text read survives only as long as the predicate is not here.**
The pre-plan `QueryAsync(dataset, bbox, filter, ct)` member is renamed
`QueryFilterAsync` on this branch: SpatialEngine-u2x.8 lands the plan's
`Predicate? Where` member and the boundary text parser, and with them this
member goes. The rename is what makes the merge a union rather than a choice —
see the merge note below.

**7. The adapter pushes down only what the plan can express.**
`FeatureQueryEngine` compiles the served parameters onto the contract —
`outFields` to the projection, `orderByFields` to the order, `resultOffset` /
`resultRecordCount` / `resultPaginationToken` to the page start and cap,
`returnCountOnly` to a count, `returnDistinctValues` to a distinct,
`outStatistics` + `groupByFieldsForStatistics` to a grouped aggregate — and uses
the store's own reduction face when it has one. It pushes the plan only when
the *whole* match is expressible, which means: no `time` and no topological
`spatialRel` (ADR-0074 §8 — `time` compiling to a disjunction of range tests is
SpatialEngine-u2x.11). The Esri `where` grammar *is* in the plan now: it
compiles to the plan's `Predicate? Where` (ADR-0074 §7, ADR-0083) and is
pushed whenever `EsriWhereResolver` says the clause is a pushdown, which is
exactly when the layer's `OBJECTID` is store-derived. A request with a residual
— a clause the facade has to number rows against, a `time`, a topological
`spatialRel` — keeps the scan-and-match path unchanged. The match envelope's
remaining members becoming pushdown is SpatialEngine-u2x.11, not this decision.

## Merge note (SpatialEngine-u2x.8)

Both beads rewrite `IFeatureStore` from the same base. `FeatureQuery` is
merged as the **union** of their members — `.8` contributes `Predicate? Where`,
this bead contributes the shaping members and the page return type — and
`QueryFilterAsync` is dropped. Taking either side's copy wholesale will not
compile. The union is SpatialEngine-u2x.9.1 and it settled three things this
record left open:

- **The reference is one piece of code.** The reference executor and the
  reference predicate evaluator sit together in `Spatial.Querying`
  (`FeaturePlanExecutor`, `ReferencePredicate`) rather than the evaluator
  living in one store. `Select` applies the predicate; `Finish` is the shaping
  half alone, for a store that already applied the restriction in its own
  dialect. Nothing is evaluated twice and nothing is dropped.
- **The restriction and the shaping compose, and a store must prove it.** The
  pushdown-equals-reference suite gained plans that *carry* a predicate —
  matched, unmatched and `IS NULL` — beside and with the order, the cap and the
  box, for the read and for every reduction. A SQL Server conformance suite
  joins the PostGIS one, so both SQL providers are measured against the
  reference rather than only against each other.
- **The identity rule has a second copy, inside the store.** A PostGIS or SQL
  Server dataset with no identity column names its features by the ordinal of
  the read, exactly as a layer whose `OBJECTID` is the scan ordinal does at the
  facade. A `WHERE` that reached SQL would renumber them, so both stores keep
  the restriction and select over the whole read for such a dataset
  (ADR-0083). The suite compares feature identities, not only values, so a
  renumbering is a red test rather than a surprise in production.

## Consequences

- **`returnCountOnly`, a grouped `outStatistics` and a paged `orderByFields`
  query stop materialising the dataset** where a store can push them: the PostGIS
  provider issues a `COUNT(*)`, a `GROUP BY`, and a `LIMIT`/`OFFSET` read
  instead of a full scan, which is the depth the bead asks for and the
  measurement the spike in SpatialEngine-u2x.1 established.
- **Every store passes one conformance suite.** The suite compares a store's
  own answer with `Spatial.Querying`'s over a fixture with ties, nulls,
  single-row groups and an empty set, so a dialect that drifts is a failing
  test rather than a different number in production.
- **The adapter gets smaller, not larger.** The reduction fan-out moves from
  `FeatureStatisticsEngine` and `FeatureResponseWriter` to one compilation
  point; the in-memory group/sort/dedupe code is what the store now does.
- **`Spatial.Querying` is a new project** in the solution and in the
  implementation allowlist — a deliberate structural choice, recorded here
  because ADR-0074 left the placement of implementation-side plan semantics
  open.
- **Not decided here:** pushing `DISTINCT` and the SQL Server reductions into
  their dialects (both are follow-ups; the answers are already right, only the
  cost is not yet reduced), the match-envelope pushdown (SpatialEngine-u2x.11),
  and retiring the `filter` text from the HTTP API in favour of a JSON plan
  (ADR-0074's deferred breaking change).

## Alternatives

- **Put the executor in `Spatial.Core`.** It is evaluation, and Core is
  structural (ADR-0074 §2). Rejected.
- **Give the in-memory providers each a copy, as the predicate evaluator
  ended up.** Rejected: two implementations of one answer is how the two
  provider filter grammars happened.
- **Make `IFeatureAggregateStore` required on `IFeatureStore`.** Rejected in
  ADR-0074 §6; restated here because the aggregate face is now real: the
  demo and ArcGIS REST stores would have to implement SQL aggregation to satisfy
  a member none of them needs.
- **Define the distinct and group order as "ascending by the key"** so SQL
  could always express it. Rejected: it silently reorders a served response
  that asked for the store's own order, which is the one thing principle 15
  forbids. The store offers the pushdown only for a plan that asks.
- **Issue the page cursor as a bare offset.** Rejected: a client-authored
  offset is not a store-issued token, and a cursor carried to a different plan
  would page into a different question.
- **Have the adapter compute a residual filter over the pushed page.** Rejected:
  paging happens before the residual, so a residual would drop rows from a page
  that was already cut — the classic wrong-answer bug. A plan the store cannot
  fully answer is not paged.

## References

- Principles 6 (contracts outlive implementations), 8 (no implementation type
  crosses a boundary), 15 (pushdown optional, semantics-preserving), 17 (small
  kernel).
- ADR-0074 §1, §5, §6 (the plan, the page, the reduction face), ADR-0033 (the
  optional additive face), ADR-0057 (statistics capability honesty), ADR-0028
  (parameterised SQL), ADR-0038 (`IFeatureLookup`, an earlier additive read
  face), ADR-0023/ADR-0029 (batch pages).
- `src/Spatial.Contracts/IDataStores.cs`,
  `src/Spatial.Contracts/IFeatureAggregateStore.cs`,
  `src/Spatial.Contracts/FeatureQueryValidation.cs`,
  `src/Spatial.Querying/`, `src/Spatial.Stores.PostGIS/PostgisPlanReader.cs`,
  `src/Spatial.Adapter.GeoServices/FeatureQueryEngine.cs`,
  `tests/architecture/Spatial.Architecture.Tests/ArchitectureGuardTests.cs`,
  `architecture/references/geoservices-compatibility.md`
