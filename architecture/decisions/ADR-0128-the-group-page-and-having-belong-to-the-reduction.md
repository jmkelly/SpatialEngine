---
status: accepted
date: 2026-09-30
deciders: maintainer + agent
summary: The group page and the `having` clause belong to the **reduction**, not the plan: `AggregateQuery` carries `Having`/`Limit`/`Offset` and `AggregatePage` carries `HasMore`, because a plan's cap would cut rows the store never grouped (ADR-0098 §7 as amended by SpatialEngine-u2x.9.2). The clause is the one predicate vocabulary over the *group row* — the group fields and the statistics' result names — so a store that implements the reduction face must answer both, with no second capability face; Postgres spells it as the dialect's own aggregate expression in the grouped statement's `HAVING`, before its `LIMIT` (amends 0098, 0115).
amends: ADR-0098, ADR-0115
---

# ADR-0128: The group page and the `having` clause belong to the reduction, and a store that implements the face answers both

## Context

ADR-0115 ended with the one part of the served `outStatistics` request it did
not push down, and named this bead as the place the decision is made:

> **Not decided here:** the page over groups and `having`, which are still
> adapter-side over the groups the store returned — pushing the cap needs a
> capability or contract decision this bead did not authorise
> (SpatialEngine-u2x.44).

The gap is real and it is not small. A statistics query with
`resultRecordCount=1` over a grouped reduction asked the store for **every**
group, built them all in managed code and skipped all but one, and a `having`
clause filtered them there too. On a layer with a million distinct
`groupByFieldsForStatistics` values that is a million rows over the wire to
write one.

Three things had to be settled before it could be, and the bead said so:

1. `having` is an Esri clause over *statistic aliases* (`total > 150`), which is
   not a SQL `HAVING` over columns — so either the adapter compiles it into one
   (a mapping from alias to the aggregate expression, which the dialect's own
   aggregate spelling makes dialect-specific) or the page stays after it.
   Pushing the page and keeping `having` in the adapter is **wrong**, because
   the cap would be cut before the filter: page 1 of 1 of a filtered set is not
   the same page as page 1 of 1 of the whole set.
2. Nothing in the contract said a store honours a page on a reduction, so the
   adapter could not push a cap and *assume* it: either the reduction face grew
   an explicit group-page member, or the adapter kept the cap.
3. The plan cannot carry either of them — ADR-0098 §7 as amended by
   SpatialEngine-u2x.9.2 says so, and it is right: a cap the *plan* asked for
   cuts rows the store never grouped.

The reasoning in that amendment is about the plan, and it holds. What it left is
that the served cap went un-pushed because the reduction face had nowhere to
carry it, so this record is the answer to "where does the group page live".

## Decision

**1. The group page and the clause are members of the reduction, not of the
plan.** `AggregateQuery` grows `Having`, `Limit` and `Offset`, and
`AggregatePage` grows `HasMore`:

```csharp
public sealed record AggregateQuery(
    IReadOnlyList<AggregateSpec> Specs,
    IReadOnlyList<string>? GroupBy = null,
    Predicate? Having = null,
    int? Limit = null,
    int? Offset = null);
```

This is the amendment's own argument, completed rather than contradicted: the
cap cannot be a plan member *because* it cuts groups rather than rows, and the
reduction is the value that is a set of groups. A store reading a plan and a
reduction is therefore asked two separate questions — which rows, and which of
their groups — instead of one question it could only half answer.

**2. The clause is the one predicate vocabulary, over the group row.** A
`Having` is a `Predicate` (ADR-0074 §2), so it is parsed once at the boundary
into the same tree the plan's `where` uses, and it is evaluated by the same
evaluator: the reference builds a feature from the group's key values followed
by one value per statistic and calls `ReferencePredicate.Matches` over it
(`FeatureReduction.MatchesGroup`). There is no second definition of what
`total > 150` means, on any path.

Its vocabulary is the **group row**: the group fields and the statistics'
result names, and nothing else (`AggregateQuery.GroupRowFields`). A name that is
neither is `invalid.arguments` (`FeatureQueryValidation.ValidateHaving`), not a
clause that matches no group — a typo has to fail on the stores that filter in
SQL too, or the answer depends on which store served it.

**3. The two are applied in that order, and the order is the reason they are one
member.** The clause decides which groups exist; the cap cuts those. The
reference filters, then pages, then reports `HasMore` from what it cut — and the
served surface asks the store for exactly that, so a pushed answer and an
in-memory one are the same page of the same set.

**4. A store that implements `IFeatureAggregateStore` answers both, and there is
no second face and no flag.** The interface is the capability — that is the
ADR-0033 additive-face pattern the face was built on, and the same obligation
`IFeatureStore.QueryAsync` already has for the plan's own cap. Honouring the
page is not a *pushdown* requirement: a store that reduces in managed code owes
the same page as one that writes a `GROUP BY`, which is why the memory, demo,
ArcGIS REST and SQL Server stores get it by going through the reference and the
conformance suite holds them to it. A capability face (`IPagedAggregateStore`)
was the alternative and was rejected: it would make the common path — the one
every in-memory provider takes — a second contract to implement and probe, for
a rule that is not optional in the first place.

**5. The total is the store's own business on a paged reduction; the flag is
not.** A store that pushed the page has not counted the groups the cap cut, so
`AggregatePage.TotalCount` is `null` there and the paging question is answered
by `HasMore` instead — which a store answers by asking for one row past the
page and dropping it. The conformance suite therefore compares `TotalCount`
where it is a question every store can answer (an unpaged reduction) and
compares `HasMore` always.

**6. Postgres spells the clause as the dialect's own aggregate expression, in
the grouped statement, before its `LIMIT`.** `GROUP BY`, `HAVING`, `ORDER BY`
and `LIMIT` compose in that order, so the clause and the cap are one statement
and the server assembles no group past the page. A name the clause uses is a
group column — the key's own column, under the byte-order collation every string
comparison in that store states (ADR-0121, ADR-0123) — or a result name, which
is not a column at all and is written as the same aggregate expression the
select list carries (`HAVING SUM("population") > @p0`). That is the only
dialect-specific half; the tree walk, the operators, the literal binding and the
three-valued reading of a null are the predicate compiler's, over a resolution
callback instead of a schema. A clause naming a value the group row does not
carry is **declined** (`null`), so the caller reduces the rows it read with the
reference — the same "a cost, never a different answer" rule the group order
already uses.

**7. The served surface validates the clause where it compiles the request.**
`FeatureStatisticsEngine.Compile` checks every name against the group row and
rejects it with the message the `orderByFields` check already uses, so the
pushed path and the match path fail the same clause the same way — which is the
invariant the whole `outStatistics` pushdown is measured by.

**8. A store that reduces in managed code is handed the plan's order, and the
reference applies it — which is what the writer's re-sort was doing for it.**
Once the writer stopped re-sorting the pushed path's groups, the only place the
order could come from was the store, and a store that reduces over rows it read
had no way to apply it: `FeaturePlanExecutor.Select` restricts and does not
order, by design (a reduction face must see the whole selected set). So
`FeatureReduction.Aggregate` takes the plan's order as an optional argument and
applies it to the *groups* when every term names a group field — the same gate
the pushed path uses to decide whether it can order at all (ADR-0115 §4), and
the same two admissible sequences the conformance suite states. Ordering the
rows instead would not do: a group has no value of its own for a term the group
row does not carry, which is why the gate is on the *terms* and not on the data.

This is stated here because it is a semantic change to the reference, made
visible by this bead: a store reducing in managed code under a plan ordered by
its group key now answers the *plan's* group order rather than the scan's. That
is the contract's answer, and it is what makes the served response the same
whichever store is behind the layer.

## Consequences

- **A paged `outStatistics` query costs one page of groups.** The adapter asks
  the store for the page and the clause and writes the rows it got back;
  `exceededTransferLimit` is the store's own "one more group" and the
  `resultPaginationToken` is still the surface's own offset token over the
  filtered set, exactly as it has always been for statistics.
- **The match path keeps the clause and the cap** for a request the plan cannot
  carry (a `where` the facade has to number rows against, a `time`, a group
  order the plan cannot state), and the JSON it writes is the same writer — so
  the two paths differ only in where the numbers came from, which is the
  invariant `FeatureStatisticsPushdownTests` compares body for body.
- **A provider that implements the reduction face has three more members to
  honour**, and `FeatureReduction` is where the rules are written, so the
  in-memory providers are correct by construction and the SQL ones are measured
  against them by `Spatial.QueryConformance` (nine clause and page shapes, under
  a plan a `GROUP BY` can answer and one it cannot).
- **A `having` clause naming a statistic's result in a different case is still
  `invalid.arguments`**, as it was on the match path: the contract's vocabulary
  is ordinal and the served surface's rejection is now stated in one place.
- **The SQL Server store's reduction is still in managed code**
  (SpatialEngine-u2x.45), and it answers the clause and the page correctly
  because it goes through the reference — it simply does not push them yet.
- **Not decided here:** the statistic `orderBy` over a group
  (`orderByFields=total DESC`). It is still refused rather than pushed, because
  a group row's *order by its own aggregate* is not a `GROUP BY` order at all
  (ADR-0115 §4) and the match path's `ApplyStatisticOrder` is what serves it.
## Alternatives

- **Leave the cap on the plan and have the store cut rows before it groups.**
  Rejected, and it is the amendment's own argument: `LIMIT` on a plan over a
  grouped query is a cap on the *input*, so a cap the statistics response means
  as a cap on the output would answer a different question.
- **A separate `IPagedAggregateStore` capability face, probed like the
  reduction face itself.** Rejected: the obligation is not optional for a store
  that reduces, and a second face would leave the common in-memory path
  unimplemented by default — the exact shape ADR-0115 §"the suite measures the
  pushdown" was written to catch.
- **A subquery: `SELECT * FROM (SELECT … GROUP BY …) g WHERE total > 150
  ORDER BY … LIMIT …`.** Portable and honest about aliases, and it keeps the
  clause away from the dialect's aggregate spelling — but it makes the outer
  `ORDER BY` name output columns, which is ambiguous the moment a result name
  collides with a group field, and it puts a sort inside a subquery under a
  `LIMIT`, which the SQL standard does not promise to preserve. The single
  statement composes in the documented order and has neither problem.
- **Compile a statistic alias to a `HAVING` over the aggregate and reject every
  clause the dialect cannot spell.** Rejected: the spelling is per statistic and
  per dialect, so a new statistic would need a new spelling in every provider
  before a clause over it could be pushed at all. Spelling the whole aggregate
  expression — which the provider has already written for the select list — is
  the same work for every statistic.
- **Keep `having` in the adapter and push only the cap.** Rejected explicitly by
  this bead's context, and it is the one combination that is *silently* wrong:
  the cap is cut before the filter, so the page is a page of the wrong set and
  the answer is not the reference's.

## References

- Principles 6 (contracts outlive implementations), 15 (pushdown optional,
  semantics-preserving), 17 (small kernel).
- ADR-0098 §3 (the reduction semantics), §7 as amended by SpatialEngine-u2x.9.2
  (why the plan carries no page), ADR-0115 (the ungrouped reduction, the null
  rules, the order gate, and the page/`having` this record settles), ADR-0074
  §2/§4/§6 (the predicate vocabulary, the reference evaluator, the reduction
  face), ADR-0033 (the additive-face pattern), ADR-0121/ADR-0123 (the byte order
  every string comparison states), ADR-0028 (parameterised SQL).
- `src/Spatial.Core/Features/Query/FeatureAggregates.cs`,
  `src/Spatial.Contracts/IFeatureAggregateStore.cs`,
  `src/Spatial.Contracts/FeatureQueryValidation.cs`,
  `src/Spatial.Querying/FeatureReduction.cs`,
  `src/Spatial.Adapter.GeoServices/FeatureStatisticsEngine.cs`,
  `src/Spatial.Adapter.GeoServices/StoreQueryPath.cs`,
  `src/Spatial.Stores.PostGIS/Core/PostgisPlanQueries.cs`,
  `src/Spatial.Stores.PostGIS/Core/PostgisPredicateSql.cs`,
  `src/Spatial.Stores.PostGIS/PostgisPlanReader.cs`,
  `tests/conformance/Spatial.QueryConformance/QueryConformanceSuite.cs`,
  `tests/unit/Spatial.Stores.Memory.Tests/ReferencePlanSemanticsTests.cs`,
  `tests/unit/Spatial.Stores.PostGIS.Tests/PostgisPlanSurfaceTests.cs`,
  `tests/unit/Spatial.Adapter.GeoServices.Tests/FeatureStatisticsPushdownTests.cs`,
  `tests/integration/Spatial.PostGIS.Tests/PostgisStatisticsPushdownIntegrationTests.cs`.
