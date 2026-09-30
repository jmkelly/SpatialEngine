---
status: accepted
date: 2026-09-29
deciders: maintainer + agent
summary: A statistics reduction is the store's own aggregate, and the reference says what a group of one answers: `var` and `stddev` are null, not zero.
---

# ADR-0115: The statistics reduction is the store's aggregate, and the reference says what a group of one answers

## Context

ADR-0098 §7 and its amendment (SpatialEngine-u2x.9.2) put the served
`outStatistics` request on the store's reduction face, and ADR-0110 put the
*match envelope* on the plan. What was left after both was the part neither
could reach: the request was compiled, and the store was asked, but the
PostGIS provider declined most of what it was asked.

`PostgisPlanReader.AggregateAsync` pushed a grouped reduction only when the
plan carried an order, and `PostgisPlanQueries.Aggregate` refused to write SQL
without one. An ungrouped reduction — the most ordinary statistics request
there is, one group, no order — therefore never reached SQL at all: the
provider read every matched row over the wire and reduced them in managed code.
And the `where is not null` gate in front of it meant a dataset with no
identity column (ADR-0097) never pushed anything.

The conformance suite passed while all of this was true, because it compares a
store's answer with the reference's and the *answers agreed*: the ungrouped
reduction was reduced in memory, by the reference, over rows the store had
read. The suite was measuring the fallback, not the pushdown. That is the
failure mode this bead set out to close, and the reason the suite had to be
widened before the store was fixed rather than after.

Three questions came out of writing that suite, and all three are decisions
rather than code.

## Decision

**1. The reference says null where the sample form is undefined, and the
pushdown reproduces it.** `var` and `stddev` are the *sample* forms: they
divide by n − 1, and a group of one non-null value has no such division. Both
references answered `0` — the population form of a group the sample form cannot
describe, reached by guarding the division rather than by deciding what an
undefined statistic is. SQL answered `null`, because `VAR_SAMP` over one value
is null. The reference is now null (`FeatureReduction.Sample`, and the
adapter's own copy in `FeatureStatisticsEngine`), for a group with no non-null
value exactly as for an empty group.

This is a served change: a `var`/`stddev` outStatistic on a single-row group
writes `null` where it wrote `0`. It is the change the compatibility surface
makes anyway — every other statistic with nothing to reduce is null — and
`0` was the one that disagreed with its own rule.

**2. A count of nothing is null, so a dialect's `COUNT(field)` of zero is
translated.** "Nulls are skipped, not counted as zero: a count is the number of
non-null values" (ADR-0098 §3) has a second half that was only half-stated: a
group whose every value is null has *no values to count*, and every statistic
with no non-null input is a null — the count included. The reference already
said so and the dialect says `0`, so `FeatureReduction.Counted` is the one place
that turns the dialect's zero into the contract's null. Stated here because
ADR-0098 §3's sentence reads the other way round, and the next reader should
know which half wins: the null is not counted as a zero *and* a count of no
values is a null rather than a zero.

**3. An ungrouped reduction is one aggregate row, and a reduction over no rows
is one row of nulls.** The plan's order is not written into an ungrouped
aggregate at all (an `ORDER BY` over an ungrouped aggregate's column is a
query Postgres refuses), an ungrouped reduction is offered whether or not the
plan carries an order, and the SQL is written for the case the restriction is
null *because there is no restriction* — a dataset whose restriction the
dialect cannot carry (ADR-0097's ordinal rule) still reduces in memory, or the
`WHERE` would silently not be there.

The empty set then needs a deliberate step in both directions. SQL returns *no
row* for a grouped reduction over no rows, and the contract's answer there is
no groups: they agree. SQL returns *one row* for an ungrouped reduction over no
rows, and the contract's answer is one row of nulls: they do not, and the
provider composes the null row from the row count the request asked for
(`FeatureReduction.EmptyGroup`). A request that asked for no row count — one
whose every other statistic is null either way — is left as the dialect
returned it, which is already the null row.

**4. A group order is pushed only when the plan's order is over the group key.**
`PostgisPlanQueries.Aggregate` used to *add* the plan's ordered columns to the
`GROUP BY`, because an `ORDER BY` expression must be grouped. For the served
statistics path that was invisible — the adapter only offers a grouped
reduction when `orderByFields` names exactly the group fields — and for a
caller that orders a reduction by something else it is a different question:
grouping by the order's own column returns *more groups* than the reference
does, with the same key twice. The order is now a gate rather than a grouping
instruction: a grouped reduction whose order names anything the group key does
not carry is reduced here, where the order is knowable.

**5. The suite measures the pushdown, and compares a group sequence against the
orders the contract allows.** Two changes to
`Spatial.QueryConformance`, both of which found a defect in the store rather
than in the suite:

- **A reduction's row order is a set plus a sequence from a small set of
  admissible orders.** The groups are compared as a *set*, always — that is
  what catches an extra grouping, a dropped group, or a row for a group that
  does not exist — and the sequence is accepted either as the reference's
  first-seen order (the fallback store) or as the plan's order with the
  untaken group columns appended ascending (the pushed store). Comparing
  sequences strictly would have failed the fallback stores for being correct,
  and comparing sets only would have let a store answer in any order at all.
- **The group key is a nullable field that is neither the key nor the
  numeric**, so the fixture's groups hold several rows, one of them has two
  *distinct* values (a percentile has to interpolate), and one is a
  null-keyed group of its own. Grouping by the numeric made every group a
  single row, which answers nothing about interpolation or about a variance
  that has to divide. The plans now include one whose order is the group key,
  in both directions, because nulls-last-ascending and nulls-first-descending
  are two rules and the ascending one is the default.

**6. A double reduction is compared to twelve significant digits; everything
else exactly.** Postgres reduces in `numeric` and converts once at the end, so
`STDDEV_SAMP` comes back as the last representable digit either side of the
reference's `sqrt` of the same variance. A store that pushes a reduction down
cannot promise better than the double's own representation, and a suite that
compared doubles as text would fail on a difference no JSON client can see. A
count, a sum, an extreme, a percentile, a key and a null are still compared
exactly: those have no such slack.

## Consequences

- **An ungrouped statistics query on PostGIS is one `SELECT COUNT(*), SUM(…),
  AVG(…), VAR_SAMP(…), …` and no rows over the wire.** The integration test
  asserts it as a call count (`ScanAsync` zero, one aggregate) beside the value
  comparison, because a suite that only compared values would have passed with
  the fallback it was written to catch.
- **A grouped statistics query is a `GROUP BY` with the plan's order**, and a
  plan whose order is not over the group key is reduced here. An order the
  group row does not carry is not an answer a store can be asked for.
- **Every store's conformance suite now exercises the pushed paths**, because
  the fixture's fields were re-chosen so the reductions push: PostGIS, SQL
  Server, the ArcGIS REST store, the demo store and the memory store all run
  the same suite and all pass, which is the point of it living in
  `Spatial.Querying`.
- **A `var`/`stddev` over a single-row group is now `null` on the served
  surface**, and a `count` of an all-null group is `null` rather than `0`. Both
  are the answers the in-memory reference already gave, so a client that
  compared the two paths sees them agree where they used to differ.
- **The store's own reduction face is where these rules are stated**, which
  means a new provider has to reproduce them: `FeatureReduction.EmptyGroup` and
  `FeatureReduction.Counted` are the two calls a SQL-pushing provider makes,
  and the conformance suite is what holds it to them.
- **Not decided here:** the page over groups and `having`, which are still
  adapter-side over the groups the store returned — pushing the cap needs a
  capability or contract decision this bead did not authorise
  (SpatialEngine-u2x.44). The SQL Server provider's reduction is still
  in-memory (SpatialEngine-u2x.45), and a pushed-down text sort key is still
  ordered by the database's collation rather than ordinally
  (SpatialEngine-u2x.43) — both named there, neither fixed here.

## Alternatives

- **Keep `0` for a single-row group and make the store answer `0`.** Would
  mean asking a dialect for something `VAR_SAMP` does not compute (coalescing a
  null away, or `VAR_POP`, which is a different statistic), and it would keep
  the one rule the surface does not hold. Rejected.
- **Leave the ungrouped reduction in memory because the plan has no order.**
  There is one group, so its order cannot differ; the "no order" rule exists
  for *grouped* reductions (`GROUP BY` returns rows in no defined order) and
  does not apply. Rejected, and it is the whole cost the bead is for.
- **Ask the adapter for the row count on every ungrouped request** (it already
  does, ADR-0098's amendment) versus **composing the null row from whatever
  the dialect returned.** The probe is the honest one, because a row count of
  zero is the only thing that distinguishes "no rows" from "a group of nulls",
  and a request that asked for no count has nothing to distinguish with. Both
  are kept, for exactly that reason.
- **Compare the pushed aggregate to the reference *after* sorting both by the
  plan's order.** Hides the very thing the comparison is for: a store that
  groups wrongly still sorts to the same set of rows.
- **Assert a double reduction with a relative tolerance instead of rounding the
  rendering.** Same promise, but it would have to pick a tolerance in two
  places (the suite and the served-body comparison) and could disagree with
  itself; rounding once, in the one place that renders a reduced value, keeps
  a single number.

## References

- Principles 6 (contracts outlive implementations), 15 (pushdown optional,
  semantics-preserving), 17 (small kernel).
- ADR-0098 §3 (the reduction semantics), §7 as amended by SpatialEngine-u2x.9.2
  (when a grouped `outStatistics` is offered), ADR-0110 (the match envelope),
  ADR-0097 (the identity rule inside a store), ADR-0074 §4/§6 (the plan read
  and the reduction face), ADR-0057 (the statistics surface and the percentile
  statistics), ADR-0028 (parameterised SQL).
- `src/Spatial.Stores.PostGIS/PostgisPlanReader.cs`,
  `src/Spatial.Stores.PostGIS/Core/PostgisPlanQueries.cs`,
  `src/Spatial.Querying/FeatureReduction.cs`,
  `src/Spatial.Adapter.GeoServices/FeatureStatisticsEngine.cs`,
  `tests/conformance/Spatial.QueryConformance/QueryConformanceSuite.cs`,
  `tests/conformance/Spatial.QueryConformance/QueryFixture.cs`,
  `tests/integration/Spatial.PostGIS.Tests/PostgisStatisticsPushdownIntegrationTests.cs`,
  `tests/unit/Spatial.Adapter.GeoServices.Tests/FeatureStatisticsPushdownTests.cs`,
  `tests/unit/Spatial.Stores.Memory.Tests/ReferencePlanSemanticsTests.cs`.
