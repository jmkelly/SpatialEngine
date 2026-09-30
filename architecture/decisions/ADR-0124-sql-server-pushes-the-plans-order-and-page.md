---
status: accepted
date: 2026-09-29
deciders: maintainer + agent
amends: ADR-0116, ADR-0121
---

# ADR-0124: The SQL Server store pushes the plan's order and page, and writes the reference's two ordering rules into the `ORDER BY`

## Context

ADR-0074 made the feature read a plan and ADR-0098 pushed that plan onto the
stores. Two of the four dialect stores — the in-memory provider and SQL Server
— had no page pushdown at all, and ADR-0116 §1 fixed that for the in-memory one
and left the second to a follow-up. The PostGIS path was done first
(SpatialEngine-u2x.10) and the record named the reason in its own words:

> reproducing the reference's null ordering and collation from T-SQL `ORDER BY`
> needs its own argument (and the keyset/`OFFSET`-`FETCH` shape needs a total
> order, the same rule PostGIS now applies).

ADR-0121 wrote that argument for the text half, and left this store
"deliberately unchanged, and deliberately so: the store pushes the
*restriction* and finishes the plan with the reference executor over the rows it
selected, so it has no T-SQL `ORDER BY` to be wrong about … when that pushdown
is written, this record is the argument it has to answer (`Latin1_General_100_BIN2`
for the Windows collations SQL Server ships by default)".

So the gap was a large-layer read on a second dialect: an ordered, capped plan
over a 200k-row table fetched all 200k features, ordered them in process and
threw 199 000 of them away, where the same plan on PostGIS is one capped
statement. What the pushdown had to reproduce is exactly two rules, both of
which T-SQL states the other way round.

**Nulls.** T-SQL sorts a null as the lowest value there is: first ascending,
last descending. The contract sorts nulls *last* ascending and *first*
descending (`AttributeValueComparer.CompareNulls`, ADR-0098 §3) — the opposite
in both directions, so neither a plain `ASC` nor a plain `DESC` is ever right.

**Collation.** A `varchar`/`nvarchar` column carries the database default
collation unless it declares its own, and the collations SQL Server ships by
default are a case-insensitive locale comparison. `SQL_Latin1_General_CP1_CI_AS`
folds `"A"` onto `"a"` at the primary level and sorts `_c` where its letters
sort, where the contract's ordinal comparison puts `A, A, _c, a, a` and
`en_US.utf8` puts `a, a, A, A, _c` (ADR-0121 §1). The fixture in
`Spatial.QueryConformance` is built so the two disagree on every row.

**The tie-break.** ADR-0098 §3 makes the feature identity the final ascending
key, and `FeaturePlanExecutor` implements it as a comparison on
`Feature.Id.Value` — the feature id *string*, joined from the identity columns
with `|`. That matters: two rows whose sort key ties are ordered `"10"` before
`"9"`, which an `ORDER BY` over the identity *column* would reverse.

## Decision

1. **An ordered capped plan is one statement.** `SqlServerPlanReader` compiles
   the plan's order and page into `SELECT … WHERE … ORDER BY … OFFSET n ROWS
   FETCH NEXT m ROWS ONLY`, counts the matched rows with a `SELECT COUNT(*)`,
   and answers the page with its batches, its `HasMore`, its store-issued cursor
   and the exact total (ADR-0116 §2). The rows that cross the wire are a page;
   the answer is the reference's answer either way.

2. **Every sort term leads with a null-placement key.** A term is written
   `CASE WHEN [c] IS NULL THEN 1 ELSE 0 END, [c] ASC` (and the mirror for
   `DESC`, whose nulls the contract places first). Nothing is inherited: the
   `CASE` is on every term whatever the column's type and whatever the column's
   nullability, because the schema the store discovered is the schema a reader
   hands it and a plan's key is not the store's to declare non-null.

3. **A text term is read under the code-point collation.**
   `CONVERT(nvarchar(max), [c]) COLLATE Latin1_General_100_BIN2`, as ADR-0121
   §2 named for this store. The term is skipped when the database already
   compares by code point, decided by a probe —
   `SELECT CONVERT(nvarchar(128), DATABASEPROPERTYEX(DB_NAME(), 'Collation'))`
   — read once per store and cached on the storage, with a failed or unread
   probe answered *as a locale collation* because that is the direction that
   costs a planner step rather than the answer. Only a `_BIN2` collation counts
   as a code-point comparison: `_BIN` agrees only inside its own code page, and
   a `_UTF8` collation orders by UTF-8 code point, which puts a supplementary
   character where an ordinal comparison of UTF-16 code units does not.

4. **The term also converts.** `text` and `ntext` columns cannot be compared or
   sorted at all in T-SQL ("the text, ntext and image data types cannot be
   compared or sorted"), and the store reads tables it did not create. Reading
   every text term through `CONVERT(nvarchar(max), …)` is what makes a
   hand-authored `text` column a page rather than a `store.unavailable`.

5. **The tie-break is the feature id string, and it is rendered, not
   approximated.** The identity columns are rendered the way
   `SqlServerDiagnostics.FeatureIdentity` renders a feature id —
   `value?.ToString()` per column, joined with `|` (so a composite identity is
   one `CONCAT`), under the code-point collation *unconditionally*, because the
   reference compares ids ordinally whatever the database does. A column T-SQL
   renders as other text than .NET does disqualifies the pushdown rather than
   answering a different question: a `float` converts with six significant
   digits where `double.ToString()` keeps every one, a `bit` is `1`/`0` where
   .NET writes `True`, a `datetimeoffset` formats by the server's settings in
   one and by the culture in the other. `Int64`, `String` and `Guid` render the
   same on both sides (a `uniqueidentifier` with `LOWER(CONVERT(nvarchar(36),
   …))`, since T-SQL writes it upper-case and `Guid.ToString()` does not), and
   those are the identities whose plans are pushed.

6. **A page needs a total order, and T-SQL will say which one it has.**
   `OFFSET`/`FETCH NEXT` is legal in T-SQL only over an `ORDER BY`. So the
   store pushes a page when, and only when, the plan asked for an order this
   table can make total — requested terms plus the identity tie-break, the
   renderable identity of §5 included. Everything else is finished by the
   reference executor over the rows the restriction selected, which is what
   this store did before and is still the right answer. In particular **an
   unordered plan is never paged in SQL**, which is where this store is
   narrower than the PostGIS one: an `OFFSET` needs an `ORDER BY` to skip over,
   and the only order T-SQL would accept there is one the store would be
   inventing, while an unordered plan's order is its scan order.

7. **A plan with several requested sort keys is not paged yet, and the reason
   is a defect in the reference rather than in this dialect.**
   `FeaturePlanExecutor.Order` applies each requested key with a fresh
   `OrderBy`, so the order the reference computes over a composite plan is its
   *last* key's — `ThenSort` sits right below it, documenting that an `OrderBy`
   there would discard the keys already applied, which is exactly what the loop
   does. A pushed composite order is the contract's own order (ADR-0098 §3,
   ADR-0074 §4), so pushing one would answer a different question from the one
   this store answers today for every plan. The page is therefore pushed for
   the orders the reference and the pushdown agree on — every single-key order —
   and a composite order is finished in process until the reference is fixed
   (SpatialEngine-u2x.54). The gap is invisible to the conformance suite today
   because neither provider's `CreateAsync` gives the conformance table a
   primary key, so no plan over it is ever pushed.

8. **The identity is read over the whole schema.** The pushed read's shape is
   the plan's projection plus the identity columns the projection did not
   already carry, and the row mapper's identity indexes are the identity
   columns' positions in what was *read*. An ordinary read (no projection)
   already carries them, and taking the appended columns as the identity would
   name every feature by its row ordinal — the defect
   SpatialEngine-u2x.55 records in the PostGIS reader, which does take the
   appended set.

## Consequences

- An ordered, capped plan over a large SQL Server layer is a capped read with
  `OFFSET`/`FETCH NEXT`, the `COUNT(*)` that says whether more remains, and a
  cursor the next statement can name. The statement shape is pinned in
  `SqlServerPlanPagingTests`, the order's three parts in
  `SqlServerTextOrderTests`, and the walk against a real database in
  `SqlServerPagedReadTests` — including a paged walk over a *text* key, which
  is where a collation drift shows up at a page boundary.
- The pushed page is measured against the reference by the shared conformance
  suite, which the SQL Server provider now also runs over a table that carries
  a primary key (`SqlServerQueryConformanceTests`): a key is the one thing a
  pushed page needs, and without that case the suite never leaves the
  reference path on either SQL provider.
- **A pushed sort over a text column cannot use an index on this provider.**
  Every string column this store creates is `nvarchar(max)`, which SQL Server
  refuses as an index key (ADR-0092), so a keyed table that a deployment wrote
  by hand pays a sort for an ordered text read. That is the price of the
  contract's answer (principle 15), exactly as ADR-0121 records for PostGIS.
- An unordered plan, a plan over a table with no primary key, a plan whose
  identity this dialect renders differently, and a composite-order plan are all
  still read whole and finished in process. Each is a fallback with the same
  answer, and each is a named gap rather than a silent one.
- A store that is asked for the same plan twice on a connection whose
  collation changed mid-flight keeps the first answer for the store's life; the
  probe is a property of the database, read once, as ADR-0121 §2 decided for
  the other provider.

## Rejected

- **Order the page by the primary key when the plan asked for no order.** It is
  what makes an `OFFSET` legal over an unrestricted plan, and it is a different
  answer: an unordered plan's order is its scan order, and the reference
  computes over that. A page in the contract's order is a store's invention.
- **Reproduce the reference's last-key composite order in T-SQL.** It would keep
  the store self-consistent with today's reference and would write a bug into
  every statement this provider issues, on the strength of a defect in the
  thing the store is measured against. The reference is what should change.
- **`ORDER BY … COLLATE <the column's own collation>` discovered per column.**
  It is the more precise rule, and `sys.columns.collation_name` has it; it needs
  the discovered type modifier to reach the plan, which is ADR-0121's own
  not-decided item (SpatialEngine-u2x.49) and applies to both providers.
- **`OFFSET` for an unordered plan, with a keyset cursor later.** A keyset
  cursor (ADR-0116's third not-decided item) needs a total order to be a
  position, so it does not reach the case this rule excludes.

## References

- Principles 6 (contracts outlive implementations), 10 (stores are providers),
  15 (pushdown optional, semantics-preserving), 17 (small kernel).
- ADR-0074 §4-5 (the plan, the reference executor, the cursor), ADR-0092
  (indexes), ADR-0097 (pushdown and literal binding), ADR-0098 §3 (the ordering
  rule), ADR-0116 §1-2 (the paged read, the "one more" signal), ADR-0121 (the
  collation argument this record answers).
- `src/Spatial.Stores.SqlServer/Core/SqlServerPlanQueries.cs`,
  `src/Spatial.Stores.SqlServer/Core/SqlServerTextCollation.cs`,
  `src/Spatial.Stores.SqlServer/SqlServerPlanReader.cs`,
  `src/Spatial.Stores.SqlServer/SqlServerStorage.cs`,
  `tests/unit/Spatial.Stores.SqlServer.Tests/SqlServerPlanPagingTests.cs`,
  `tests/unit/Spatial.Stores.SqlServer.Tests/SqlServerTextOrderTests.cs`,
  `tests/integration/Spatial.SqlServer.Tests/SqlServerPagedReadTests.cs`,
  `tests/integration/Spatial.SqlServer.Tests/SqlServerQueryConformanceTests.cs`.
