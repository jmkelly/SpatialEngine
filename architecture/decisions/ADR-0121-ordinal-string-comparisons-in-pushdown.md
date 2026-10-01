---
status: accepted
date: 2026-09-29
deciders: maintainer + agent
summary: Every string comparison a pushed plan writes says which order it wants — `COLLATE "C"` on a string column — and never inherits the database's.
amends: ADR-0098
---

# ADR-0121: A pushed-down string comparison is a byte comparison — the database's collation is never inherited

## Context

ADR-0098 §3 stated the store query surface's ordering rule in one line:

> nulls sort last ascending (first descending); **strings compare ordinally,
> never by a locale collation**; a store appends the feature identity as a
> final ascending key.

`Spatial.Querying.AttributeValueComparer` implements it — `string.CompareOrdinal`
for `AttributeKind.String` — and the shared reference executor honours it, so
every in-memory provider is right by construction. The PostGIS provider pushes
its sort keys into SQL instead, and SQL says nothing about any of this:

- a `text` column carries the **database's** default collation, and a database
  created from the stock template is `en_US.utf8`;
- `en_US.utf8` sorts `a, a, A, A, _c` where the contract sorts
  `A, A, _c, a, a` — it folds case at the primary level and sorts the
  punctuation to where its letters sort;
- `ORDER BY`, `MIN`/`MAX` over a `text` column, a `GROUP BY`'s group order and a
  `PERCENTILE_DISC`'s `WITHIN GROUP` order are **all** collation-ordered
  operations.

The store wrote its order terms out by hand precisely because Postgres differs
from the contract elsewhere — `NULLS LAST` ascending, `NULLS FIRST` descending
are written explicitly rather than inherited — and left the one difference that
does not change a *key set* to be inherited. That is exactly the difference
that changes an *answer*: every value in the page is right and the sequence is
someone else's.

It was found while serving the statistics reduction from the store path
(SpatialEngine-u2x.13, ADR-0115), and it was invisible because the fixture's
text columns were built to order the same either way. The group key was
`a, a, b, c, null` and the label `alpha … foxtrot`: all lower-case, all
distinct letters, all in alphabetical order. The only text-ordered cases were
grouped reductions, and every locale collation in use agrees with a byte
comparison on those six values.

## Decision

**1. Every string comparison the PostGIS provider writes says which order it
wants.** One helper, `PostgisPlanQueries.Ordered`, renders a column as an
expression that compares by bytes: the quoted column, and for a
`AttributeKind.String` column an explicit `COLLATE "C"`. Every string
comparison in the file goes through it:

- a plan's `ORDER BY` terms, ascending and descending;
- the identity tie-break appended to make the order total — a text primary key
  is ordered by bytes too, or a page boundary is cut on a total order the store
  and the next page do not share;
- the group-key tie-break a `GROUP BY` appends;
- a percentile's `WITHIN GROUP (ORDER BY …)`, which decides *which row* a
  discrete percentile answers with;
- `MIN` and `MAX` over a text column, which are collation-ordered operations
  and were returning a different row's value from the same reduction.

**2. The term is skipped when the database already compares by bytes.** A
`C`/`POSIX` database sorts text the way the reference does, and
`COLLATE "C"` there is a no-op that still costs the planner a default-collation
index. `PostgisTextCollation.IsByteOrder` is that test, and the store reads the
answer from the database rather than guessing it, because the fixture container
may be either: `SELECT datcollate FROM pg_database WHERE datname =
current_database()`, once per store, cached (`PostgisStorage.DatabaseCollationAsync`).
A probe that fails, or a database that reports no collation, is treated as a
**locale** collation — the term is added. The two failure directions are not
symmetric, and this is the one that keeps the answer.

**3. A non-text column never takes a collation.** The term follows the field's
kind, not the database's collation: `COLLATE` is a string operator and
`ORDER BY population COLLATE "C"` is a statement Postgres refuses.

**4. The conformance fixture makes the two orders disagree, and the suite
compares the sequence.** `QueryFixture`'s group key is now `a, a, A, A, null,
_c` and its label `Alpha, bravo, _charlie, Charlie, a-delta, echo`: case and
punctuation, so no two rows sit in the same place under both rules. The suite
gained the text order cases that follow from it — text sort keys ascending and
descending, text as the tie-break of a composite order, and a second paged walk
over a text key, because a page boundary is where a collation drift shows up.
The group *shape* is unchanged (two groups of two rows, a null-keyed group of
its own, a single-row group), so the percentile-interpolation and
variance-divides cases are still measured.

## Consequences

- A pushed-down text sort is right on a `C` database and on a locale database,
  and the store is the same code either way: the difference is one cached
  catalog read per store, not a deployment mode.
- **The pushdown costs a sort on a locale database.** A btree index on a text
  column is built with that column's collation, so an `ORDER BY` that overrides
  it cannot use the index and the planner sorts. An ordered text read on a
  locale-collated database is therefore a sequential scan plus a sort where it
  was an index scan. That is the price of the contract's answer (principle 15),
  and it is not free: a deployment that needs both wants a `C`-collation
  database, which is a database-level decision this record does not make. A
  `text_pattern_ops` index would restore an index scan, but its order is
  "byte order for prefix matching", which is not the reference's order for
  punctuation, so it would trade the answer back for the index.
- `PostgisTextOrderTests` pins the statement in both shapes — with and without
  the term — and `PostgisQueryConformanceTests` pins the value the store reads
  from the container, its caching, and that a cancelled probe caches nothing.
- SQL Server is unchanged, and deliberately so: the store pushes the
  *restriction* and finishes the plan with the reference executor over the rows
  it selected, so it has no T-SQL `ORDER BY` to be wrong about. Its
  comment already named collation as one of the things a pushdown there would
  have to reproduce; when that pushdown is written, this record is the argument
  it has to answer (`Latin1_General_100_BIN2` for the Windows collations SQL
  Server ships by default).

## Not decided

- **String comparisons in a pushed `WHERE`.** `PostgisPredicateSql` compiles
  `code < 'delta'` as a plain comparison, which is collation-ordered for the
  same reason and returns the same rows the reference would not. The predicate
  conformance suite's string cases are all lower-case and cannot see it. Same
  defect class, different statement, tracked as its own bead.
- **A column that declares a collation of its own.** The probe reads the
  *database's* collation, which is right for a column that declares none — the
  case for every table this store creates, and every one the conformance suite
  seeds. A hand-authored table with `"label" text COLLATE "de-x-icu"` sorts by
  the column's collation whatever the database's, and the fix is a per-column
  term driven by the discovered type modifier. Tracked as its own bead.
- The SQL Server order pushdown itself, and its `DISTINCT` and grouped
  reductions, are the follow-ups ADR-0098 §4 and ADR-0116 already named.

## Alternatives

- **Always write `COLLATE "C"`.** One less input and no catalog read. Rejected
  for the cost reason in the consequences: on a `C` database it changes a sort
  the index could serve into one it cannot, for an answer that was already
  right. It is also wrong in the other direction — it cannot be "always" if a
  future store is pointed at a database whose sort we would like to keep, and
  the decision of what the database is belongs to the database.
- **Sort text in the store after reading, the way SQL Server does.** Correct
  and it costs the whole match set, which is the thing ADR-0116 exists to stop.
  Rejected as a general answer; it stays the right answer for a dialect that
  cannot express the order at all, which is the rule the whole pushdown
  surface already follows.
- **Read the values, compare them in managed code, keep the SQL order.** The
  same whole-match-set cost, plus a claim that the pushdown still earned
  something. Rejected.
- **Change the contract to the database's collation.** Rejected: the contract's
  ordering is a cross-store answer. Memory, Demo, ArcGIS REST and the reference
  executor all compare ordinally, and a store that is right only against its own
  database's locale is not a store that is right.
- **Declare the fixture's text columns `COLLATE "C"` at create time and change
  nothing else.** It would make the table right rather than the query, and it
  would only ever be right for tables this store created — a pushed-down
  `ORDER BY` over an authored table is exactly the case that goes wrong today.

## References

- Principles 6 (contracts outlive implementations), 10 (stores are providers),
  15 (pushdown optional, semantics-preserving), 17 (small kernel).
- ADR-0028 (parameterised SQL, no client text), ADR-0074 §4 (the plan
  contract), ADR-0092 (PostGIS indexes), ADR-0098 §3 and §4 (the ordering rule
  and the pushdown surface), ADR-0115 (the statistics reduction as the store's
  aggregate), ADR-0116 (the page is a position).
- `src/Spatial.Stores.PostGIS/Core/PostgisPlanQueries.cs`,
  `src/Spatial.Stores.PostGIS/Core/PostgisTextCollation.cs`,
  `src/Spatial.Stores.PostGIS/PostgisStorage.cs`,
  `src/Spatial.Stores.PostGIS/Data/PostgisQueries.cs`,
  `src/Spatial.Stores.SqlServer/SqlServerStore.cs`,
  `src/Spatial.Querying/AttributeValueComparer.cs`,
  `tests/conformance/Spatial.QueryConformance/QueryFixture.cs`,
  `tests/conformance/Spatial.QueryConformance/QueryConformanceSuite.cs`,
  `tests/unit/Spatial.Stores.PostGIS.Tests/PostgisTextOrderTests.cs`,
  `tests/integration/Spatial.PostGIS.Tests/PostgisQueryConformanceTests.cs`.
