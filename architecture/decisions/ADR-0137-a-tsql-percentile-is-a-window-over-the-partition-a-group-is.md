---
status: accepted
date: 2026-09-30
deciders: maintainer + agent
summary: A **T-SQL percentile is a window function over a derived table**, `PARTITION BY` the group key, which the grouped statement then reduces with a `MAX` — so the last statistic the dialect has no *aggregate* for is pushed as the window it is, and a `PERCENTILE_CONT/DISC` over a field T-SQL cannot rank against another is still declined (error 402); a **boolean extreme is an extreme over the `bit`'s own integers**, so the missing `MIN`/`MAX` was a missing spelling and not a missing statistic. The fraction is a bound value and the window's alias is its own position, never a client result name; the group row is reported **in the request's order**, which the flat statement got wrong and the conformance suite could not see. Supersedes ADR-0133 §3's percentile and boolean refusals, and its `having` obstruction with them — this store compiles no `having` for this dialect, so it never shared a statement (amends 0133).
amends: ADR-0133
amended-by: ADR-0157
---

# ADR-0137: A T-SQL percentile is a window over the partition a group is, and a boolean extreme is an extreme over its integers

## Context

ADR-0133 pushed the SQL Server store's three reduction faces into T-SQL and
named, in the same record, the statistics it could not: §3 declined a percentile
because "T-SQL has no ordered-set aggregate — `PERCENTILE_CONT` is a window
function over a whole result set, not an aggregate over one group", and the
`Rejected` section declined it again on the grounds that writing it would mean a
window over the grouped statement with a `PARTITION BY` over the group key, and
"a `having` clause cannot reference a windowed expression, so the two members
cannot both be pushed by one statement".

Both of those refusals are half a fact each, and the halves are what this record
completes. The true fact is that a window function cannot be written *over the
grouped statement* — its ordering column has to be a grouped column, and the
ranked field is not one (error 8120, measured). It can be written one statement
*below* it, over a derived table partitioned by the group key, and the grouped
statement then reduces the one value each partition ranked. The other true fact
is that a `having` clause cannot reference a windowed expression — which is
moot, because this store does not compile a `having` clause for this dialect at
all (ADR-0133 §3). The obstruction was the shape of the statement, not the
statistics.

And the boolean extreme is not a dialect gap at all. T-SQL has no `MIN`/`MAX`
over a `bit` — the type is not orderable there — but a `bit` *is* two integers,
and the contract's order over a boolean is false before true, which is `0` before
`1`. ADR-0133 read the missing `MIN` as the missing statistic; it is a missing
spelling.

So an `outStatistics` request on SQL Server still read the whole layer for two
statistics that are the ones a statistics request is most often for: the
served surface advertises `percentile_cont` and `percentile_disc`
(ADR-0057, `EsriFeatureQuery`), and every request naming one came back as a full
T-SQL read folded in managed code. The reference semantics were never in
question — the conformance suite passes the percentile plans today, because the
fallback *is* the reference, which is the failure mode ADR-0115 §"found while
serving" named.

## Decision

1. **A percentile is a window function over a derived table, and the grouped
   statement reduces it.** T-SQL spells a percentile
   `PERCENTILE_CONT(f) WITHIN GROUP (ORDER BY …) OVER (PARTITION BY …)`, and
   `SqlServerPlanQueries.Aggregate` writes it as
   `SELECT <key>, <statistic> …, MAX([d].[w0]) FROM (SELECT <key>, <columns>,
   PERCENTILE_CONT(@p) WITHIN GROUP (ORDER BY [c] ASC) OVER (PARTITION BY <key>)
   AS [w0] FROM … WHERE …) AS d GROUP BY <key> ORDER BY … OFFSET … FETCH …`.
   A partition is a group, and every row of a partition ranked the same value,
   so the maximum over the partition is that value and the statistic is the
   reference's. An ungrouped reduction is one partition over everything —
   `OVER ()` — and one aggregate row over the derived table, which is the
   statement it already was.

2. **The partition, the `GROUP BY` and the group order are the same value read
   on the two sides of the subquery.** The partition is written over the base
   table's key *expression* — the `CONVERT`d, `BIN2`-collated text column this
   store states everywhere else (ADR-0121) — and the `GROUP BY` and the order
   over the derived column, which carries that expression's collation with it.
   A partition written over the bare column would fold `"Alpha"` onto `"alpha"`
   and report one group where the reference reports two, and an order written
   over the bare column would be a locale order. The derived table carries the
   group key and the columns the other statistics reduce, so every expression
   above it names the derived columns.

3. **A percentile over a field this dialect cannot rank against another is
   declined, and only that.** T-SQL refuses a percentile whose ordering column
   and whose value are of different types (error 402, measured), which is every
   text, boolean, geometry, date and GUID field; a reduction that asks for one
   is finished with the shared reference over the rows it read. A field the
   dataset does not have is declined for the same reason as everywhere else. The
   fraction is a bound parameter, never text (ADR-0028), and the window's alias
   is its own position in the request — a result name is a client's text and
   never becomes an identifier.

4. **A boolean extreme is `MIN`/`MAX` over the `bit`'s own integers, and the
   value is read back as the boolean the reference reports.** `MIN(CONVERT(int,
   [flag]))` is false over a group with a false in it and null over a group with
   no value at all, which is the contract's answer for a reduction of nothing.
   The mapping is the reader's, through the `ResultKind` the reference reports
   the statistic as — so a pushed-down extreme and a reference extreme are the
   same value, and the assertion is over the boolean rather than over a `0`.

5. **The group row is reported in the request's order, not the statement's.**
   The group row is read positionally, so the select list lists one expression
   per requested statistic in the order the request asked for them — including a
   `MAX` over a window column sitting between two aggregates. It was the other
   way round first (aggregates, then windows) and the shared conformance suite
   did not catch it, because the suite's reduction still contains an envelope
   and so is reduced in process; the SQL Server statistics test caught it
   against a real database, with a plausible-looking number in the wrong column.

## Consequences

- **An `outStatistics` request on SQL Server that names a percentile or a
  boolean extreme is one statement, and no row crosses the wire.** The
  statement shapes are pinned in `SqlServerReductionPushdownTests`; the answers
  are measured against the reference by `SqlServerStatisticsPushdownTests`
  against a live SQL Server 2022, over a fixture whose groups are the ones a
  percentile is easy to get wrong — one that interpolates between two rows, one
  with nothing to rank, single-member groups, a null-keyed group and a
  descending rank — and with a call count beside every value comparison, because
  a suite that only compared values would pass the fallback it was written to
  catch.
- **The `Rejected` bullet of ADR-0133 about the percentile is superseded, and
  its `having` reason with it.** The obstruction was the statement's shape and
  not the statistics, and the `having` clause this store does not compile for
  this dialect is unchanged by any of it. A `having` clause over a *pushed*
  reduction is still a named gap, and now it is the only one in this record's
  neighbourhood: the clause resolves a name to the statistic's own aggregate
  expression, and for a percentile that expression is a window over a derived
  table rather than an aggregate over the grouped one.
- **A boolean extreme is pushed, so the only statistics this store still
  reduces in managed code are the envelope, a `having` clause, a page over an
  ungrouped reduction, and a group key or distinct field that is a geometry.**
  The envelope is the one with an expression available: `geometry::`
  `UnionAggregate` over a geometry column exists in SQL Server 2012 and later
  and answers a `MULTIPOINT`, which `.STEnvelope()` turns into the rectangle
  ADR-0120 asks for — measured against a container, and filed as a follow-up
  rather than taken here, because it is a second geometry expression and a
  second set of SRID facts for one statistic.
- **The conformance suite does not measure this path yet, and the reason is
  named.** Its reduction carries the envelope, so every plan it asks for is
  still reduced in process on this store; the pushed percentile is measured by
  the SQL Server statistics test until the envelope lands, at which point the
  shared suite measures it too.
- **The cost of the windowed statement is a sort the flat one did not pay**: the
  derived table's partitions are ranked before the `GROUP BY` groups them. It is
  paid in the server rather than in the process's memory, on rows that never
  cross the wire at all, which is the trade every pushdown in this store makes.

## Rejected

- **`PERCENTILE_CONT(…) WITHIN GROUP (ORDER BY …) OVER (PARTITION BY …)` written
  directly over the grouped statement.** T-SQL refuses it, with a message that
  names the cause: a window function's ordering column has to be contained in
  the `GROUP BY`. This is the fact ADR-0133 recorded and read as "no ordered-set
  aggregate"; the aggregate it is not, and the window it is, one statement
  lower.
- **A `DISTINCT` over the derived table instead of a `MAX` over the partition.**
  A `SELECT DISTINCT <key>, [w0]` is a shorter statement and refuses more often
  — T-SQL takes an `ORDER BY` over a `DISTINCT` only from its own select list,
  and the contract's null-placement key is not one of the requested fields
  (ADR-0133 §8, the same rule). The `MAX` is over a value every row of a
  partition ranked identically, so it is not a guess about which row wins.
- **Pushing a percentile over a text field by ranking a conversion of it.**
  The reference's percentile is over numbers (`AsDouble`), the served surface
  refuses a percentile statistic on a non-numeric field
  (`EsriFeatureQuery`), and the store's own answer for a field it cannot rank is
  the reference's. A conversion would be a second spelling of a statistic whose
  question is a rank among numbers.
- **Pushing the envelope here as well.** It has an expression, it is measured,
  and it is still a second geometry reader for one statistic; ADR-0133's
  rejection of it stands and the follow-up says what changed.

## References

- Principles 6 (contracts outlive implementations), 10 (stores are providers),
  15 (pushdown optional, semantics-preserving), 17 (small kernel).
- ADR-0057 (the statistics surface and its percentile statistics), ADR-0073
  (the SQL Server provider), ADR-0074 §6 (the reduction face), ADR-0098 §3 (the
  reduction semantics), ADR-0115 (the statistics reduction and the reference's
  null rules), ADR-0120 (the envelope as a reduction), ADR-0121 (the collation
  argument), ADR-0128 §8 (the plan's order belongs to the reduction), ADR-0133
  (the reductions this one completes, and the two refusals it supersedes).
- `src/Spatial.Stores.SqlServer/Core/SqlServerPlanQueries.cs`,
  `src/Spatial.Stores.SqlServer/SqlServerPlanReader.cs`,
  `src/Spatial.Stores.SqlServer/SqlServerStore.cs`,
  `src/Spatial.Stores.PostGIS/Core/PostgisPlanQueries.cs` (the same statistic in
  the other dialect, as an ordered-set aggregate),
  `src/Spatial.Querying/FeatureReduction.cs`,
  `tests/unit/Spatial.Stores.SqlServer.Tests/SqlServerReductionPushdownTests.cs`,
  `tests/integration/Spatial.SqlServer.Tests/SqlServerStatisticsPushdownTests.cs`.
