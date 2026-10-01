---
status: accepted
date: 2026-09-30
deciders: maintainer + agent
summary: The SQL Server store pushes its three reduction faces rather than reading a layer to fold it in process: a `COUNT(*)` over the pushed restriction, a `GROUP BY` whose group order is written before the `OFFSET`/`FETCH NEXT` that cuts it, and a `DISTINCT` on **both** providers — pushed exactly where the plan's order is *total over the deduplicated fields*, which is the same rule `FeatureReduction.Distinct` applies to the reference, so a distinct set under an ordered plan answers in the plan's order instead of first-seen. T-SQL states both of the contract's rules the other way round (nulls lowest, a case-insensitive collation), so every group key, group order, extreme and deduplicated text value is written under `Latin1_General_100_BIN2` with an explicit null-placement key; a mean is `SUM(…)/COUNT(…)` because `AVG` over an integer column is integer division. A percentile, an envelope, a `having` clause, a boolean `MIN`/`MAX` and a geometry key are declined by name and finished with the shared reference (amends 0098, 0115, 0124).
amends: ADR-0098, ADR-0115, ADR-0124
amended-by: ADR-0136, ADR-0137, ADR-0157
---

# ADR-0133: The SQL Server store pushes its reductions, and a distinct set is pushed only where the plan's order is total over it

## Context

ADR-0074 §4-§6 made the plan a plan and ADR-0098 pushed it onto the stores,
one at a time. The PostGIS provider went first and got the count, the distinct
set and the grouped aggregate; the SQL Server provider got the plan read, the
order and the page (ADR-0124) and kept the three reduction faces as a full
T-SQL read folded in managed code. That was correct and unhurried, which is a
cost decision and not a correctness one — the same kind of decision ADR-0124
was about, one face earlier.

So the gap here is the size of the three reductions: a `returnCountOnly` over a
200k-row layer read 200k features to answer with a number, and an
`outStatistics` query with `groupByFieldsForStatistics` built every group in
process. T-SQL has all three. What it does not have is the contract's two
ordering rules, and both are the wrong way round from Postgres's:

**Nulls.** T-SQL sorts a null as the lowest value there is: first ascending,
last descending. The contract sorts nulls *last* ascending and *first*
descending (ADR-0098 §3, `AttributeValueComparer.CompareNulls`) — the opposite in
both directions, exactly as ADR-0124 §"Nulls" recorded for the plan read. Every
term of a group order therefore leads with the same `CASE WHEN … IS NULL` key
the plan read already writes, and a group page is cut with `OFFSET`/`FETCH NEXT`
*after* that `ORDER BY`, because T-SQL takes the page clause only over an order
(ADR-0124 §6).

**Collation.** A `nvarchar` column carries the database's default collation,
and the collations SQL Server ships by default fold `"A"` onto `"a"`. That
matters twice over for a reduction and not once: a `GROUP BY [category]` groups
`"A"` and `"a"` into one group where the reference groups them as two values,
and a `MIN`/`MAX` over a text column answers a locale minimum. Both are written
under `Latin1_General_100_BIN2`, as ADR-0121 named for this store.

And two arithmetic facts, which are the only places where a pushed statistic is
not simply the column's own type:

- `SUM` over an integer column is an `int` sum, which overflows in the server
  where the reference's `long` sum does not — the store reads tables it did not
  create (ADR-0073), so a hand-authored `int` column is a real shape.
- `AVG` over an integer column is **integer division**: a truncated mean, and
  not the reference's at all. The mean is taken as `SUM(…float…) / COUNT(…)`
  instead, which is the reference's own `Average()` in T-SQL's `float` — the
  53-bit double — summed once and divided once at the end.

**The distinct set** was the other half of the bead, and it is a PostGIS one:
that store reduced a distinct request over the pushed-down read rather than
issuing SQL `DISTINCT`, on the ground that a dialect returns distinct rows in no
defined order while the contract's order for them is the plan's. That ground is
right, and it is also the whole rule: the reference reports distinct rows in the
order it first saw them, and when the plan's order is applied first — which it
is not, in the code as it stood — that is the order of the rows themselves.

## Decision

1. **`COUNT(*)` is the count.** `SqlServerStore.CountAsync` compiles the plan's
   restriction into a `SELECT COUNT(*) … WHERE …` and answers with the number.
   The restriction is safe to push here even for a table with no identity
   column, because a count names no feature — the ordinal such a table's rows
   carry is a number this answer does not report (ADR-0097 does not apply to a
   face that reports no ids).

2. **The grouped reduction is a `GROUP BY`, with the reference's two rules
   written out.** `SqlServerPlanQueries.Aggregate` builds
   `SELECT <key>, <statistic> … FROM … WHERE … GROUP BY <key> ORDER BY <terms>
   OFFSET … FETCH NEXT … ONLY`, where the group key of a text column is the
   converted, `BIN2`-collated expression — so the grouping, the key the row is
   read as, and the group order are one expression, and T-SQL's rule that a
   grouped `ORDER BY` may name nothing the `GROUP BY` does not group is
   satisfied rather than tripped over (the first statement written for this
   store was exactly that error, against a real container, and is why the
   statement is pinned). A statistic is the dialect's own spelling:
   `COUNT(*)`, `COUNT([c])`, `SUM(CONVERT(bigint, …))`, `SUM(…)/COUNT(…)` for a
   mean, `VAR`/`STDEV` for the sample forms the reference reports, and
   `MIN`/`MAX` over the same collated expression every other string comparison
   in this store states. The reduced value is mapped back to the kind the
   reference reports it as, so a sum over an integer field is an integer on both
   sides (ADR-0133's arithmetic, above).

3. **A reduction T-SQL cannot state is finished here, and the refusal is
   named.** `Aggregate` answers `null` — the cue to reduce the rows it read with
   the shared reference — for a *grouped* reduction whose plan asked for no
   order or asked for one over a value a group row does not carry; for a group
   key or a statistic whose kind T-SQL groups or reduces differently (a
   geometry, which groups by its stored bytes here and by its value there); for
   a statistic with no T-SQL aggregate at all — a percentile, because T-SQL has
   no ordered-set aggregate (`PERCENTILE_CONT` is a window function over a whole
   result set, not an aggregate over one group), and an envelope, because a
   rectangle is four reduced coordinates and a polygon rather than one
   expression; and for a `having` clause, which resolves a name to a
   statistic's own aggregate expression and asks this store's predicate
   compiler a second question it does not yet answer. Each is a cost, never a
   different answer.

   > **Amended by ADR-0137** on two of these. A percentile *is* pushed now — as
   > a window function over a derived table partitioned by the group key, which
   > the grouped statement then reduces, so "T-SQL has no ordered-set
   > aggregate" was true of the aggregate and not of the statistic. A boolean
   > `MIN`/`MAX` is pushed too: it is an extreme over the `bit`'s own integers.
   > The envelope and the `having` clause are unchanged.

4. **A page over a reduction is the group order and the cap in one statement,
   in that order.** The pushed statement carries the plan's group order and its
   `OFFSET`/`FETCH NEXT` together, the order first — ADR-0128 §8, and the
   reason a managed-code reduction must be handed the plan's order rather than
   reading it off the rows. One row past the cap is how the store answers "are
   there more groups?", and the total is left uncomputed on a paged reduction
   for the reason ADR-0128 gave.

5. **The ungrouped reduction of an empty selection is one row of nulls.**
   An ungrouped aggregate always returns a row and its `COUNT(*)` of that row is
   a zero, where the contract's answer is a null (ADR-0098 §3), so the row is
   replaced by the null row when the row count says no rows were selected. A
   `COUNT(field)` of zero becomes a null for the same reason. This is the
   PostGIS reader's rule, stated once and now stated twice — one per dialect.

6. **A page over an ungrouped reduction is finished here.** It is one group, so
   it needs no order, and T-SQL refuses `OFFSET`/`FETCH NEXT` without one; the
   only order it would accept there is one the store would be inventing over a
   single row (ADR-0124 §6 again).

7. **The contract's order for a distinct set is the plan's order where that
   order is total over the distinct rows — and only there.**
   `FeatureReduction.Distinct` takes the plan's order and applies it when every
   term names a requested field *and* the terms between them cover every
   requested field; the rows are unique per combination, so such an order is
   total and two callers cannot both be right about it. Any other plan — no
   order, or an order naming fewer than the requested fields — leaves the rows
   in first-seen order, which is the store's own row order and the only one it
   states. This is the same rule `FeatureReduction.Aggregate` applies to the
   groups (ADR-0098 §3, ADR-0128 §8), and it is what makes a pushed `DISTINCT`
   the reference's answer rather than a different one.

8. **A `DISTINCT` is pushed exactly where §7 holds.** Both providers build
   `SELECT * FROM (SELECT DISTINCT <fields> FROM … WHERE …) AS d ORDER BY
   <terms>`, and both answer "reduced here" for every other plan. The order is
   on an outer statement because both dialects take an `ORDER BY` over a
   `DISTINCT` only from its own select list, and the contract's null-placement
   key is not one of the requested fields (T-SQL error 145, and Postgres's own
   rule). A text field is deduplicated *and* ordered under the code-point
   collation (`Latin1_General_100_BIN2` / `COLLATE "C"`), since a locale
   collation folds `"A"` onto `"a"` — which would be both a wrong count and a
   wrong group. A geometry field is not pushed: the dialect deduplicates it by
   its stored bytes and the reference by its value.

9. **A restriction the dialect could not express still stops the pushdown.**
   A dataset with no identity column names its features by the ordinal of the
   read, so a `WHERE` that returned only some rows would renumber them
   (ADR-0097). The PostGIS reader therefore leaves the restriction null for a
   plan that restricts, and a distinct set reduced on that null would be the
   whole table's set — so the pushdown declines for exactly that plan and the
   rows are selected and deduplicated in process, where the restriction is the
   reference's own.

## Consequences

- A count, a distinct set and a grouped reduction over a large SQL Server layer
  are one statement each, and a `returnCountOnly` over a 200k-row layer stops
  reading rows at all. The statement shapes are pinned in
  `SqlServerReductionPushdownTests` and `PostgisDistinctPushdownTests`; the
  answers are measured against the reference by the shared conformance suite on
  both providers, over a plan the suite now asks every store for — the plan
  whose order is total over the deduplicated fields, which is the plan §7
  describes and the one a store may hand to SQL.
- §7 is a **behaviour change** for every store: a distinct request under a plan
  ordered over the requested fields now answers in that order rather than in
  first-seen order. The in-memory store, the fallback and the conformance suite
  pass the plan's order through with it, and ADR-0128 §8's obligation — that a
  reduction is handed the plan's order rather than reading it off the rows — now
  covers the distinct set too.
- A SQL Server reduction that asks for an envelope, a `having` clause or a
  geometry key is still read whole and reduced in process. Each is a named gap
  rather than a silent one, and each is the cost of the contract's answer rather
  than a different answer. A percentile and a boolean `MIN`/`MAX` were named
  here too and are no longer gaps: **see the amendment below**.
- The collation probe is read only when the reduction actually reduces or
  deduplicates a text column, so a count over numbers is one round trip and not
  two.
- `Spatial.Stores.SqlServer` keeps no managed-code reduction for the faces it
  pushes, and the fallback it still owns is the shared reference — so the two
  dialects now agree on the answer by construction as well as by measurement.

## Rejected

- **`PERCENTILE_CONT(…) WITHIN GROUP (ORDER BY …) OVER (PARTITION BY …)`.**
  T-SQL's percentile is a window function, so it would have to be written over
  the grouped statement with a `PARTITION BY` over the group key — and a
  `having` clause cannot reference a windowed expression, so the two members
  cannot both be pushed by one statement. Both are declined together instead.

  > **Superseded by ADR-0137.** The first half of this refusal was right about
  > the statement and wrong about the statistic: a window function cannot be
  > written over the *grouped* statement, because its ordering column has to be
  > a grouped one, and it is written one statement below instead — over a
  > derived table partitioned by the group key, which the grouped statement then
  > reduces. The second half was moot: this store does not compile a `having`
  > clause for this dialect at all (§3), so a `having` clause never had to share
  > a statement with a percentile. The bullet's `having` obstruction is
  > withdrawn with it.
- **The envelope as `MIN`/`MAX` over `.STXMin()`/`.STYMin()` in a derived
  table.** It is expressible — four reduced coordinates and a polygon built
  from them — but the polygon has to be assembled as text, and the coordinates
  have to be rendered with an explicitly invariant format for the text to be a
  polygon. That is a second geometry reader and a second set of dialect facts
  for one statistic, and PostGIS already answers it with one expression
  (`ST_Extent`).
- **`DISTINCT` with the order columns added to the select list.** T-SQL refuses
  an `ORDER BY` over a `DISTINCT` from outside its select list, so the
  alternative is to add the sort keys to the projection — which deduplicates by
  them and returns more rows than the reference whenever the plan's order names
  a field the request did not. The outer statement is the honest shape.
- **Pushing a `DISTINCT` under any plan that asks for an order.** The rows that
  tie on the order's terms are then in an order the dialect does not state, and
  the reference's first-seen order is a different one. This was the original
  temptation the bead named, and §7-§8 are what it takes to refuse it.

## References

- Principles 6 (contracts outlive implementations), 10 (stores are providers),
  15 (pushdown optional, semantics-preserving), 17 (small kernel).
- ADR-0073 (the SQL Server provider), ADR-0074 §4-§6 (the plan and its
  reductions), ADR-0097 (pushdown and literal binding), ADR-0098 §3-§4 (the
  ordering rule and the measured dialects), ADR-0115 §4 (the reduction is the
  store's), ADR-0121 (the collation argument), ADR-0124 (the page pushdown this
  one completes), ADR-0128 §8 (the plan's order belongs to the reduction).
- `src/Spatial.Stores.SqlServer/Core/SqlServerPlanQueries.cs`,
  `src/Spatial.Stores.SqlServer/SqlServerPlanReader.cs`,
  `src/Spatial.Stores.SqlServer/SqlServerStore.cs`,
  `src/Spatial.Stores.PostGIS/Core/PostgisPlanQueries.cs`,
  `src/Spatial.Stores.PostGIS/PostgisPlanReader.cs`,
  `src/Spatial.Querying/FeatureReduction.cs`,
  `tests/conformance/Spatial.QueryConformance/QueryConformanceSuite.cs`.
