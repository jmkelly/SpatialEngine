---
status: accepted
date: 2026-09-29
deciders: maintainer + agent
summary: A pushed `WHERE` states its byte order through the same `Ordered` helper the sort keys use, so a predicate and an order cannot disagree.
amends: ADR-0121
---

# ADR-0123: A pushed-down comparison says which order it wants — the database's collation is never inherited in a `WHERE` either

## Context

ADR-0121 made every string comparison the PostGIS provider *writes* say which
order it wants: the `ORDER BY` terms, the identity tie-break, the group-key
tie-break, a percentile's `WITHIN GROUP` order, and `MIN`/`MAX` over a text
column all carry `COLLATE "C"` unless the database already compares by bytes.
It said nothing about the predicate compiler, which was the same defect in the
statement that decides *which rows a query sees*.

`PostgisPredicateSql` compiled `code < 'delta'` as a plain
`"code" < @p0`, and Postgres answered it under the column's collation — the
database's default, which on a stock template is `en_US.utf8`. The contract's
answer is a byte comparison (ADR-0098 §3), and the reference evaluator gives
it, so the same plan selected a different row set than every in-memory
provider selects it.

Nothing caught it, for two independent reasons, and both had to be fixed
before the fix could be confirmed:

- **The fixture could not see it.** The predicate conformance rows were
  `alpha, beta, gamma, delta, epsilon` — lower-case words, in alphabetical
  order. Every locale collation in use agrees with a byte comparison on those
  five values, so the suite was measuring nothing. (This is the same blind
  spot ADR-0121 found in the ordering fixture, one file over.)
- **The suite was not even reaching the pushdown.** A dataset with no primary
  key names its features by the ordinal of the read, so the store keeps the
  restriction in the caller and finishes the plan with the reference executor
  (ADR-0097). `PredicateConformanceSuite` creates its table through
  `CreateAsync`, which emits no primary key — so over that table the pushed
  comparison was never executed and a collation-correct suite would have
  proved nothing either.

And on the SQL Server side the drift was not a corner of it: the collation
SQL Server ships by default is *case-insensitive*, so a pushed `code <
'delta'` folded `Delta` onto `delta` and a pushed `code LIKE '_elta'` matched a
row the reference never selected. That store finishes the plan with the
reference executor, but its `WHERE` is pushed unconditionally, so the
restriction itself was answering a different question.

## Decision

**1. Every string comparison a pushed `WHERE` writes states the byte order it
wants, and it is the same mechanism the sort keys use.** On the PostGIS side
the column is rendered by `PostgisPlanQueries.Ordered` — the one helper
ADR-0121 introduced — so the predicate and the `ORDER BY` cannot disagree
about what a string comparison is. There is no second decision to keep in
step. `=`, `!=`, `<`, `<=`, `>`, `>=`, `IN`, `NOT IN` and `LIKE` all carry the
term, and a non-text column never does: `COLLATE` is a string operator and
`"population" COLLATE "C" > @p0` is a statement Postgres refuses.

**2. The term is skipped on a database that already compares by bytes**, and
that answer is the one ADR-0121 already reads: one cached
`SELECT datcollate` per store, behind `PostgisStorage.ByteOrderTextAsync`, so
the two faces ask the same question of the same property and neither keeps
its own copy of it.

**3. The catalog read is paid for only by a plan that uses it.**
`PostgisPredicateSql.ComparesText` reports whether a predicate compares a text
column at all — the only part of a `WHERE` a collation can change — and the
plan reader and the scan face read the collation only when it is true. A plan
over a bounding box and a number cannot use the answer, and should not pay a
round trip for it. A null test is not a comparison of values and is not one.

**4. On SQL Server the term is unconditional, and it is a binary collation.**
There is no probe to read: nothing in the store's own metadata names a
database's collation, and the one SQL Server ships by default is the
case-folding one, so the two failure directions are not symmetric and the
answer has to be written. `Latin1_General_100_BIN2` compares code points
rather than letters, which is the nearest thing T-SQL has to the contract's
byte comparison and the only non-folding collation of the ones SQL Server
ships. This is the argument ADR-0121 named for the SQL Server order
pushdown, applied to the `WHERE` that pushdown's own plan already had.

**5. The fixture makes the two orders disagree, and the suite reaches the
pushdown.** The predicate fixture's `code` column now carries `Delta` beside
`delta` and `_bravo`: byte order is `Delta, _bravo, alpha, beta, delta,
epsilon, gamma` and `en_US.utf8` is `alpha, beta, _bravo, delta, Delta,
epsilon, gamma`, so no two of the seven rows sit in the same place under both
rules. The two new codes sit outside the box the bounding-box cases use, so
those still measure the box and nothing else. And the suite runs a second time
over a table with a primary key, which is the precondition for a `WHERE` to be
pushed at all — without it the suite measures the fallback, and a
collation-correct store would pass it just as a wrong one would.

## Consequences

- A filter's answer is the reference's answer on a `C` database and on a locale
  one, on Postgres and on SQL Server, and the store is the same code either
  way.
- **A pushed text filter can no longer seek a default-collation index.** A
  btree index on a text column is built with that column's collation, so a
  comparison or a `LIKE` that overrides it is a scan. On Postgres that is the
  same trade ADR-0121 already made for the sort key; on SQL Server it is new,
  and a deployment that needs both wants a binary-collation database, which is
  a database-level decision this record does not make. The answer is the part
  that is not negotiable (principle 15).
- The `LIKE` term is a strict improvement, not only a parity one: it is what
  keeps an ICU database — where `LIKE` folds case — matching the set the
  reference evaluator matches.
- A filter that compares a text column now costs one catalog read the first
  time a store answers such a plan. It is the read ADR-0121 introduced,
  cached, and cancelled the same way: a cancelled probe caches nothing and the
  next caller asks again.
- The fixture's `code` column is a text primary key in the pushed-where case,
  and a case-sensitive key is a precondition of that case being writable at
  all on SQL Server's default collation — which is the drift this record
  removes, but the two are the same fact seen from two sides.
- `PostgisPredicateCollationTests` and `SqlServerPredicateCollationTests` pin
  the statement in both shapes, the SQL Server one has no container here, and
  `PostgisPredicateConformanceTests` pins the value the store reads from the
  stock container, the caching, a cancelled probe, and that a cancelled
  pushdown leaves the next read correct.

## Not decided

- **A column that declares a collation of its own.** The Postgres probe reads
  the *database's* collation, which is right for a column that declares none —
  every table this store creates and every one the conformance suites seed. A
  hand-authored `"label" text COLLATE "de-x-icu"` sorts and compares under the
  column's collation whatever the database's, and the fix is a per-column term
  driven by the discovered type modifier. Tracked as its own bead, and named
  by ADR-0121.
- **The SQL Server order pushdown** (`ORDER BY`), its `DISTINCT` and its
  grouped reductions, are the follow-ups ADR-0098 §4 and ADR-0116 already
  named. ADR-0121's record is the argument they have to answer; this one is
  the same argument for the `WHERE` they will be compiled beside.

## Alternatives

- **Compare text in managed code after the pushdown.** Correct, and it reads
  the whole table to filter it. Rejected on the same grounds ADR-0121 rejected
  it for the order: it is the cost the pushdown surface exists to avoid.
- **Add a `LIKE`/`IN`-shaped exclusion and keep `LIKE` out of it.** Rejected:
  they are string comparisons of the same kind, and leaving them out means a
  store whose `LIKE` and whose `<` disagree about what a string is.
- **Probe SQL Server's collation the way the Postgres store does.** Rejected
  for the asymmetry: a probe that fails must be answered "add the term", so
  the conditional version is only ever an optimisation, and the thing it
  optimises is a statement the contract needs written anyway. A deployment
  that wants the index can declare a binary collation on the database or the
  column, and the term stays correct either way.
- **Make the fixture's text columns `COLLATE "C"` at create time.** It makes
  the table right rather than the query, and only ever for tables this store
  created — a pushed `WHERE` over an authored table is exactly the case that
  goes wrong today. (Rejected by ADR-0121 for the order; the argument is the
  same one here.)
- **Change the contract to compare by the database's collation.** Rejected:
  the contract's comparison is a cross-store answer, and a store that is
  right only against its own database's collation is not a store that is
  right.

## References

- Principles 6 (contracts outlive implementations), 10 (stores are providers),
  15 (pushdown optional, semantics-preserving), 17 (small kernel).
- ADR-0028 (parameterised SQL, no client text), ADR-0074 §3-4 and §6 (the
  predicate vocabulary and the pushdown surface), ADR-0092 (store indexes),
  ADR-0097 (the identity column and the ordinal fallback), ADR-0098 §3 (the
  ordering and comparison rule), ADR-0116 (the page is a position), ADR-0121
  (the same rule for the order, and the collation probe this record reuses).
- `src/Spatial.Stores.PostGIS/Core/PostgisPredicateSql.cs`,
  `src/Spatial.Stores.PostGIS/Core/PostgisPlanQueries.cs`,
  `src/Spatial.Stores.PostGIS/Core/PostgisTextCollation.cs`,
  `src/Spatial.Stores.PostGIS/PostgisStorage.cs`,
  `src/Spatial.Stores.PostGIS/PostgisPlanReader.cs`,
  `src/Spatial.Stores.PostGIS/PostgisFeatures.cs`,
  `src/Spatial.Stores.SqlServer/Core/SqlServerPredicateSql.cs`,
  `src/Spatial.Querying/ReferencePredicate.cs`,
  `tests/conformance/Spatial.PredicateConformance/PredicateConformanceSuite.cs`,
  `tests/unit/Spatial.Stores.PostGIS.Tests/PostgisPredicateCollationTests.cs`,
  `tests/unit/Spatial.Stores.SqlServer.Tests/SqlServerPredicateCollationTests.cs`,
  `tests/integration/Spatial.PostGIS.Tests/PostgisPredicateConformanceTests.cs`.
