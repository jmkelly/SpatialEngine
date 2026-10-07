---
status: accepted
date: 2026-10-07
deciders: maintainer + agent
summary: The adapter offers a grouped `outStatistics` reduction to the store whether or not the request names a group order: "no `orderByFields` asked for" is not "an order the store cannot return". An unordered reduction owes the response the store's own order — the reference's first-seen order over the rows the restriction selected (ADR-0098 §3, ADR-0128 §8) — not the scan order of a whole-layer read, so the natural `outStatistics` + `groupByFieldsForStatistics` request stops reading the layer and reaches `IFeatureAggregateStore`, whose grouped reduction over the restricted read is the reference's answer (ADR-0184 §1). A requested order the plan cannot state as a total order over the groups still keeps the match path (amends 0098).
amends: ADR-0098
related: ADR-0184, ADR-0128, ADR-0133, ADR-0097
---

# ADR-0194: An unordered grouped reduction is the store's to answer

## Context

ADR-0098 §7, as amended by SpatialEngine-u2x.9.2, offers a grouped
`outStatistics` reduction to the store only when `orderByFields` names exactly
the group fields. The reason was the row order: a grouped reduction answers in
the order the plan asked for, `GROUP BY` returns rows in no defined order, and a
request that asked for no order would then be answered with the first-seen order
of a match set SQL never assembled. The adapter therefore kept the match path for
the most natural client request there is — `outStatistics` +
`groupByFieldsForStatistics` with no `orderByFields` — and that request
full-scans the layer and groups it in managed code.

ADR-0184 then made the store's own reduction of a keyless dataset correct: it
pushes the restriction and finishes the grouped reduction in managed code when
the plan's order is null, so the answer is the reference's first-seen order over
the rows the restriction selected (ADR-0133 §6, ADR-0184 §1). Its measurement
(2026-10-07) put a grouped reduction at 2.8–8.8 ms / 0.02–0.08 MB against a
`page25` whole read at 136–178 ms / ~31 MB. The served adapter never asked for
it: the natural request stopped at `TryGroupOrder`, so the win was unreachable
for the request most clients send. ADR-0184's own Consequences note names
SpatialEngine-d0q as the adapter route's question.

What the routing turns on is the order, and it is a question ADR-0098 §3 already
answers: "A plan that asks for no order takes the store's own order." A store
that reduces over the rows it read keeps the reference's first-seen order —
`FeatureReduction.Aggregate` is the definition every store is measured against
(ADR-0128 §8). The old amendment read "the store's own order" as an order only
SQL could produce, and so forbade the route; it conflated the *capability* to
push a `GROUP BY` with the *obligation* to answer the reference's order. A store
that cannot state the group order declines the SQL push and reduces in managed
code, and both providers already do (ADR-0133 §3, §6).

## Decision

**1. The adapter offers a grouped `outStatistics` reduction to the store whether
or not the request names a group order.** `TryGroupOrder` returns the group
fields as the plan's order when `orderByFields` names them; it returns a null
order — not the match path — when the request names no `orderByFields`; and it
still returns the match path when the request asked for an order the plan cannot
state as a group order (a subset of the group fields, or a value the group row
does not carry). ADR-0098's amendment is refined on exactly that one point: "no
order asked for" is not "an order the store cannot return".

**2. An unordered grouped reduction owes the response the store's own order over
the rows the restriction selected, not the scan order of a whole-layer read.**
That is ADR-0098 §3 read for what it says, and it is what makes the route
semantics-preserving: the served bytes are the match path's bytes, over the
fixture the pushdown suite already compares body for body. The two agree because
the store's reduction is defined as the reference's over the rows it read
(ADR-0128 §8) and the restriction is the same plan the match path evaluates.

**3. The store's own order is not a promise of stability, and the response makes
none.** An unordered request has never carried a promised row order — the match
path's "first-seen" is the store's scan order — so a store whose restricted read
returns rows in a different physical order than its whole-table scan may serve a
different sequence for the same request. That is a property of an unordered
query, not a regression this route introduces: the response is ordered by what
the plan asked for, and the plan asked for nothing.

**4. The `having` clause and the group page ride on the reduction either way.**
They are members of the reduction, not of the plan, and the order they apply in
does not depend on whether the plan carries a group order (ADR-0128). The
unordered request is the ordered request with a null order, so the store filters
and pages the groups exactly as it does for an ordered one.

**5. What is not changed.** A request the plan cannot carry at all — a `time`, an
`objectIds`, a topological `spatialRel` — keeps the match path, as every other
verb does. A grouped reduction whose requested order is not a total order over
the groups keeps the match path, because the response owes the order the request
asked for and no store can return it. No contract type changes and no store
changes; the store face ADR-0184 decided is exactly what this route asks for.

## Alternatives

- **Keep the match path for an unordered grouped reduction.** The status quo, and
  rejected on the measurement: the most natural client request is the one that
  reads the whole layer (415 ms over HTTP, 12,202 bytes) while the store's own
  grouped reduction on the same layer is 8.8 ms / 0.08 MB. It is a routing gap,
  not a semantic one.
- **Invent a group order when the request named none** (the group key
  ascending, say). Rejected: it is a different response. The served order is
  what the request asked for, and rewriting an unordered response into a sorted
  one is the silent degradation principle 15 forbids.
- **Require the client to send `orderByFields`.** Rejected: the Esri surface
  accepts an unordered statistics query and answers it, so the engine does not
  get to narrow the request it serves to make its own pushdown hold.
- **Push a `GROUP BY` and rely on the store's SQL order.** Rejected: the
  contract's answer for a reduction is the reference's, and a dialect's
  `GROUP BY` order is not. The store must decline the SQL push when it cannot
  state the order — which both providers do — so the adapter never has to know
  which store it is talking to.

## Not decided

- **A promised order for an unordered reduction.** The contract says "the
  store's own order" and this record does not strengthen it. A client that needs
  a stable sequence asks for one, and both the match path and the store route
  answer it.
- **`orderByFields` naming a statistic's result name.** It is refused by the
  plan's field compiler before either path can order the group rows, with a
  message that differs from the writer's. It is a separate boundary question,
  not this route.

## Consequences

- **The natural statistics request stops reading the layer.** `outStatistics` +
  `groupByFieldsForStatistics` with no `orderByFields` reaches the store's
  reduction face: the restriction is pushed, the groups are reduced over the
  restricted read, and the layer is never scanned. `FeatureStatisticsPushdownTests`
  pins it as a call count — `Scans` zero, `Aggregates` one — beside the
  body-for-body comparison with the match path.
- **The served response order is unchanged in the common case and is the
  store's own in every case.** On a store whose restricted read is its scan
  order (the in-memory providers, and PostGIS's `WHERE`-preserving seq scan) the
  bytes are identical; on a store whose restriction changes the read's physical
  order, an unordered request comes back in that store's order, which is what
  the contract says it answers with.
- **The match path keeps its role.** A residual the plan cannot carry, and a
  requested group order the store cannot total, still read and group in the
  adapter; the suite holds the two paths to the same bytes.
- **ADR-0184's deferral is closed.** Its Consequences note said the adapter
  route was "a separate record's question"; this is that record, and the store
  face it measured is now the face the served surface reaches for the natural
  request.
- **The cost.** A store that does not implement `IFeatureAggregateStore` still
  falls back to the reference over the dataset, as it always did, and a store
  that implements the face but declines the SQL push reduces over the restricted
  read rather than the whole table — which is ADR-0184's win, now reachable.

## References

- ADR-0098 §3 and §7 as amended by SpatialEngine-u2x.9.2 (the plan's order and
  the grouped-reduction boundary this refines), ADR-0074 §4/§6 (the plan and the
  reduction face), ADR-0128 (the clause and the page belong to the reduction),
  ADR-0133 §3/§6 (a dialect declines a reduction it cannot state and finishes it
  with the reference), ADR-0184 §1/§Consequences (the keyless reduction pushdown
  and the adapter deferral this closes), ADR-0120 (the extent reduction),
  ADR-0097 §1 (the feature read's decline, untouched), principles 6, 15, 17.
- `src/Spatial.Adapter.GeoServices/StoreQueryPath.cs` (`StatisticsAsync`,
  `TryGroupOrder`), `src/Spatial.Adapter.GeoServices/FeatureStatisticsEngine.cs`
  (`Reduction`, `StatisticsFromGroups`),
  `src/Spatial.Querying/FeatureReduction.cs` (`Aggregate`),
  `src/Spatial.Stores.PostGIS/PostgisPlanReader.cs` (`AggregateAsync`),
  `src/Spatial.Stores.SqlServer/SqlServerPlanReader.cs`.
- `tests/unit/Spatial.Adapter.GeoServices.Tests/FeatureStatisticsPushdownTests.cs`.
- Bead SpatialEngine-d0q, answering ADR-0184's deferral; the measurement is
  SpatialEngine-8dm / ADR-0184.

## Measurements

| What | How | Result | Date |
| --- | --- | --- | --- |
| The cost the route removes | ADR-0184's 2026-10-07 re-run of `eng/spike-u2x-query-baseline.sh --store=postgis` on the 34,135-row keyless world-cities layer, the `countOnly`/`statistics` cells routed through `IFeatureAggregateStore` | a grouped reduction **2.8–8.8 ms / 0.02–0.08 MB**, 0 rows materialised, against a `page25` whole read at **136–178 ms / ~31 MB** | 2026-10-07 |
| The request as the harness's HTTP half sends it, before this route | `eng/spike-u2x-query-baseline/RESULTS.md` end-to-end table (`eng/spike-u2x-postgis-e2e.sh`, 2026-10-01/02), whose statistics request carries no `orderByFields` | global statistics **415 ms / 12,202 bytes / 171 groups**; europe **219 ms / 962 bytes / 8 groups** — a whole read, because the served face kept the match path | 2026-10-01/02 |
| The route lands | `FeatureStatisticsPushdownTests` — an unordered grouped request against a store with the reduction face: `Scans` 0, `Aggregates` 1, the reduction's `GroupBy` is the group field and the plan's `Order` is null, and the body equals the match path's over the same fixture | pinned | 2026-10-07 |
