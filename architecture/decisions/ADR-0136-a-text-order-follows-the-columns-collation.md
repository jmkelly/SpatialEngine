---
status: accepted
date: 2026-09-30
deciders: maintainer + agent
summary: A pushed-down text order follows the collation **the column actually carries**, not the collation the database has: a column that declares one keeps it, a column that declares none inherits the database's, and the store writes `COLLATE "C"` when *that* is not already byte order (so an authored `COLLATE "de-x-icu"` column on a `C` database states the order, and an authored `COLLATE "C"` column on a locale one does not). The declared collations come from `information_schema.columns.collation_name` — **not** from `format_type`, which renders a collated `text` as plain `text` — join the column metadata the discovery already reads, and are held with the description they were read for (ADR-0122), outside the contract type (ADR-0028). SQL Server needs no counterpart: it always writes `Latin1_General_100_BIN2`, which overrides whatever a column carries (amends 0121, 0123, 0126, 0133).
amends: ADR-0121, ADR-0123, ADR-0126, ADR-0133
---

# ADR-0136: A pushed-down text order follows the collation the column carries, not the collation the database has

## Context

ADR-0121 decided that a string comparison the PostGIS provider writes states
which order it wants: `COLLATE "C"` for a text sort key, an identity
tie-break, a group key, a percentile's `WITHIN GROUP`, a `MIN`/`MAX`, and (by
ADR-0123, ADR-0126) the comparisons inside a pushed `WHERE`. The term is
skipped when the database already compares by bytes, and that test is
`SELECT datcollate` — read once per store, cached, because a collation is a
property of the database rather than of a query.

The database's collation is the right answer for a column that **declares
none**, which is every table this store creates (`CREATE TABLE … text`) and
every one the conformance suite seeds. It is not the answer for a column that
declares one. `"label" text COLLATE "de-x-icu"` sorts by the *column's*
collation whatever the database's — Postgres resolves a column's collation at
DDL time and no later `datcollate` changes it — so on a database created
`LOCALE 'C'` the store skips the term, and the order it returns is the ICU
dictionary's: `_c, a, A` where the contract says `A, _c, a`.

ADR-0121 named this as its own "not decided" item and pointed at the fix. The
bead that tracked it believed the fact was already in hand, in the column type
modifiers `PostgisQueries.ColumnTypeModifiers` reads for a geometry column's
Z/M (ADR-0084). It is not: `format_type(atttypid, atttypmod)` renders
`de-x-icu`-collated text as plain `text`, because a collation is not part of a
type's modifier — the declared collation lives on the attribute, and
`information_schema.columns.collation_name` is the column that reports it (and
reports `NULL` for the columns that declare none, which is exactly the
distinction the fix needs).

## Measurement

Both orders were run against the stock `postgis/postgis:16-3.4` image, on the
fixture's own database (`en_US.utf8`) and on a database created from `template0`
with `LOCALE 'C'`, over the same three values — the smallest set on which the
two collations cannot agree by accident (`A` is 65, `_c` is 95, `a` is 97):

| column | database | `ORDER BY label` inherits | the contract |
| --- | --- | --- | --- |
| `label text` | `en_US.utf8` | `_c, a, A` | `A, _c, a` |
| `label text COLLATE "de-x-icu"` | `en_US.utf8` | `_c, a, A` | `A, _c, a` |
| `label text` | `C` | `A, _c, a` | `A, _c, a` |
| `label text COLLATE "de-x-icu"` | `C` | `_c, a, A` | `A, _c, a` |

The store's term closes the first two rows (a locale database states the order
for every text column regardless of what the column declares, because
`COLLATE "C"` in the expression overrides the column's) and the third. The
fourth row is the defect: the database is read, the term is skipped, and the
column's own collation answers.

## Decision

**1. The question a pushed-down string comparison asks is per column.** The
decision to state `COLLATE "C"` follows the collation *that column actually
carries*: its own where it declares one, the database's where it declares none.
`PostgisTextOrder` is that pair — the database's collation and the map of
declared column collations — and `PostgisPlanQueries.Ordered`, the one place
every string comparison this store writes is rendered, asks it per column.
Both shapes follow from the one rule, and the second is as much a part of it as
the first: on a locale database a column declaring `COLLATE "C"` takes no term,
because the sort is already the reference's and the term is the no-op that
costs the planner its index (ADR-0121's cost argument, unchanged).

**2. The declared collations are discovered with the schema and held with it.**
`information_schema.columns.collation_name` joins the column metadata the
discovery already reads — no extra round trip, no extra table scan — and
`PostgisSchemaDiscovery.TextCollations` keeps only the columns that declare
one. The description and its collations travel together as
`PostgisDatasetFacts` and are cached and invalidated together
(`PostgisDescriptionCache`, ADR-0122): a collation that outlived the schema it
was read for would answer for a table that is no longer there. They are *not*
in `DatasetDescription`, which is a contract type served as JSON by
`GET /api/datasets/{id}` and written in core vocabulary only.

**3. Nothing else changes shape.** The boolean every compiler took —
`bool byteOrderText` — becomes `PostgisTextOrder`, whose `Locale` and
`ByteOrder` values are the two answers it already had. The faces still read
the database's collation only when the plan compares a text column at all
(ADR-0121, ADR-0123, ADR-0126), because a plan over a number and a bounding
box cannot be changed by a collation.

## Consequences

- A pushed-down order over an authored table is the reference's order on a
  locale database and on a `C` one, and so is an identity comparison, a group
  order, a percentile and a pushed `WHERE` over the same column — they are all
  rendered through the one helper.
- The per-column cost is zero per query and one extra column per dataset on the
  description read, which is already paid per dataset (ADR-0122). The store's
  own tables declare nothing, so for every dataset it creates the map is empty
  and the behaviour is ADR-0121's exactly.
- A schema change that alters a column's collation out of band is picked up
  when the description is re-read, on the same schedule as any other schema
  change — which is the schedule the cache already documents.
- The `information_schema.columns` view hides columns the connected role has no
  privilege on. That is the same visibility the rest of discovery reads, so a
  column the store cannot describe is a column it cannot read either.
- SQL Server is untouched and needs no counterpart yet: its store always writes
  `Latin1_General_100_BIN2` (ADR-0133), which overrides whatever collation a
  column carries, so an authored column declaring a case-insensitive collation
  is corrected by the statement rather than inherited from the column.

## Not decided

- **`ILIKE`/folded patterns and the predicate grammar's own collation terms.**
  The folded pattern (ADR-0132) states its fold rather than a collation, so it
  is untouched by this; whether a pushed `WHERE` needs its *own* per-column
  treatment beyond this shared helper is the remaining half of ADR-0123
  (SpatialEngine-u2x.48).
- **A column whose collation is `und-x-icu` or another nondeterministic
  collation** compared for equality — an identity key under one is a
  case-insensitive identity (ADR-0126), which this does not change: the store
  states the byte order and the column's own collation is overridden.

## Alternatives

- **Read every column's collation as a separate catalogue query per query.**
  Rejected: it is a per-query round trip for a property of the table, and the
  discovery that reads the table's columns already reads their rows.
- **Take the collation from the type modifier, as the bead assumed.** Not
  available: `format_type` renders a collated `text` column as `text`. Measured
  before the record was written, on the fixture image.
- **Always write `COLLATE "C"` on a text column and drop the database probe
  entirely.** This is the same decision ADR-0121 rejected for the database's
  collation, for the same reason (the term costs a default-collation index on a
  sort that was already right), and it would additionally lose the second shape:
  an authored `COLLATE "C"` column on a locale database is exactly the case
  where the term is pure cost.
- **Create every table's text columns `COLLATE "C"` at create time.** ADR-0121
  rejected it, and it only ever covers tables this store created.
- **Read the column's collation from `pg_collation` and `pg_attribute`
  directly rather than `information_schema`.** Same fact, less standard surface
  and a join the standard view already does.

## References

- Principles 6 (contracts outlive implementations), 10 (stores are providers),
  15 (pushdown optional, semantics-preserving).
- ADR-0028 (parameterised SQL, no client text; `DatasetDescription`), ADR-0074
  §4 (the plan contract), ADR-0084 (the declared layout, the other catalogue
  read this one joins), ADR-0098 §3 (the ordering rule), ADR-0121 (who states
  the order, and the database probe), ADR-0122 (the description cache),
  ADR-0123 (the pushed predicate), ADR-0126 (the identity comparison),
  ADR-0132 (the folded pattern), ADR-0133 (the SQL Server binary collation).
- `src/Spatial.Stores.PostGIS/Core/PostgisTextOrder.cs`,
  `src/Spatial.Stores.PostGIS/Core/PostgisTextCollation.cs`,
  `src/Spatial.Stores.PostGIS/Core/PostgisPlanQueries.cs`,
  `src/Spatial.Stores.PostGIS/Data/PostgisQueries.cs`,
  `src/Spatial.Stores.PostGIS/Data/PostgisSchemaDiscovery.cs`,
  `src/Spatial.Stores.PostGIS/PostgisDatasetFacts.cs`,
  `src/Spatial.Stores.PostGIS/PostgisDescriptionCache.cs`,
  `src/Spatial.Stores.PostGIS/PostgisStorage.cs`,
  `tests/unit/Spatial.Stores.PostGIS.Tests/PostgisTextOrderTests.cs`,
  `tests/unit/Spatial.Stores.PostGIS.Tests/PostgisSchemaDiscoveryTests.cs`,
  `tests/integration/Spatial.PostGIS.Tests/PostgisColumnCollationOrderTests.cs`.
