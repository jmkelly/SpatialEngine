---
status: accepted
date: 2026-09-30
deciders: maintainer + agent
summary: Every statement that names a feature by a text identity column states the order it is compared in, on the term the pushed comparison already uses.
---

# ADR-0126: A text identity is a byte identity — the comparison that names a feature states the order it wants

## Context

ADR-0123 made every string comparison a pushed `WHERE` *writes* state the
order it wants: `COLLATE "C"` on Postgres unless the database already compares
by bytes, `Latin1_General_100_BIN2` on SQL Server unconditionally. It said
nothing about the half of a `WHERE` that is not a filter — the identity
restriction — and nothing about the other four statements that name a feature
by its identity at all.

A text identity column under a collation that folds case is a case-insensitive
identity. Two features the contract distinguishes — `delta` and `Delta`, which
ADR-0098 §3 compares as two different strings — become one key, and the store
then answers for one with the other:

- `GetAsync(dataset, ["delta"])` returns the feature whose key is `Delta`;
- an update of `delta` **writes to the `Delta` row** and reports success, so an
  edit of one feature rewrites another;
- a delete of `delta` removes the `Delta` row;
- the attachment probe says the feature exists, and the attachment is stored
  against it.

This is not a corner of it. It is the *shipped default*: the collation SQL
Server installs with is case-insensitive, and a `nvarchar` key column that
declares no collation of its own carries it. On Postgres the same table is one
`CREATE COLLATION ... deterministic = false` away, and a `text` key column
under that collation folds the same way.

There is a second, worse failure on SQL Server, and it is the one that made the
predicate conformance fixture unwritable. A text key under a case-folding
collation **cannot hold both codes at all**:

```
Violation of PRIMARY KEY constraint ... The duplicate key value is (Delta).
```

So the fixture's own pushed-`WHERE` case had to fall back to an integer
surrogate key, which measured a store whose identity was never text — the very
thing that goes wrong (ADR-0123 §5, and the comment in
`SqlServerPredicateConformanceTests`). A second SQL Server fact is in the same
place: the store's own string type is `nvarchar(max)`, which is not a legal
key column at all, so a text key is only ever an *authored* column.

## Decision

**1. Every statement that names a feature by a dataset's *identity column*
states the order a text identity is compared in, on the term the pushed
comparison already uses.** One helper per provider renders the identity tuple,
and every statement that names a feature that way goes through it — the pushed
id restriction, the lookup, the update's target, the delete's target and the
dataset-level feature probe the attachment faces ask before they write. There
is no second decision to keep in step, and no statement that can drift from
the others:

- `SqlServerIdentity.Tuple` writes `[code] COLLATE Latin1_General_100_BIN2 =
  @p0` for a text column, and a bare `[id] = @p0` for anything else;
- `PostgisIdentity.Tuple` writes `"code" COLLATE "C" = @p0` for a text column
  **unless this database already compares text by bytes** — the same
  conditional, and the same cached catalog read, as ADR-0123's predicate
  (ADR-0121), so the two halves of one `WHERE` cannot answer two different
  questions.

**2. The catalog read is paid for only by a statement that compares text.**
`PostgisIdentity.ComparesText` is the same question ADR-0123 asked of a
predicate, asked of an identity: a dataset keyed on a number cannot be changed
by a collation and does not read one. On SQL Server nothing is read, because
the term there is unconditional and there is no probe to read.

**3. A text identity column must *carry* a byte-order collation for the table
to hold the features the contract distinguishes, and the answer is the
authored table's to declare.** The store cannot make this true for itself: a
text identity is not a column the store can create — `IngestIdentity.Source`
requires an `Int64` field, and a `nvarchar(max)` is not a legal key — so every
text identity in existence is an authored column, and the requirement falls
there. The conformance fixture satisfies it by declaring
`Latin1_General_100_BIN2` on its `code` key and by being a `nvarchar(64)`,
which is the same escape hatch ADR-0123 named for the index. The fixture is
keyed on `code` on both providers from here.

**4. A case-folding text key is not refused.** Its loss is the *table's*, and
the store's answer to a lost feature is defined: `delta` is not `Delta`, so the
lookup returns nothing, the update reports `not.found`, the delete affects no
row. Refusing the dataset would deny reads of a table that is perfectly
readable — a store that cannot name a feature it can see is not a store that
is being careful.

## Consequences

- A feature is the feature its identity names, on a case-insensitive database
  and on a folding one, on both providers, in every statement that names one.
- **A text identity lookup can no longer seek the key index on a
  case-folding-collated column** — a comparison that overrides the column's
  collation is a scan, the same trade ADR-0121 made for the sort key and
  ADR-0123 for the filter. It is a statement about an uncommon table shape
  (a text key under a case-folding collation), and the answer is the part that
  is not negotiable (principle 15). A deployment that wants the index declares
  a binary collation on the key column, which is what the fixture now does.
- An identity comparison over a text column now costs one catalog read the first
  time a store answers such a statement on Postgres — the read ADR-0121
  introduced, cached, and cancelled the same way: a cancelled probe caches
  nothing and the next caller asks again.
- A dataset keyed on a number is unchanged: no probe, no term, the same
  statements as before.
- `SqlServerWriteOperations` and `PostgisWriteOperations` now resolve an
  update's identity predicate against the *dataset's* schema rather than the
  batch's, because a batch need not carry the identity column at all. That is
  the difference between "the batch named the key" and "the dataset's key",
  and the key is the dataset's.
- `PostgisIdentityCollationTests` and `SqlServerIdentityCollationTests` pin
  every statement in T-SQL and SQL; `PostgisIdentityCollationIntegrationTests`
  and `SqlServerIdentityCollationIntegrationTests` run against a real container
  over a table whose key column really does fold, and assert that a lookup for
  the other case finds nothing and that an edit of it changes no row.

## Not decided

- **A column that declares a collation of its own.** The Postgres term is
  decided by the *database's* collation, which is right for a column that
  declares none — every table this store creates and the conformance fixture
  alike. A hand-authored `"code" text COLLATE "de-x-icu"` identity, or one
  under a non-deterministic ICU collation in a `C` database, still decides its
  term from the database rather than from the column. The per-column term
  driven by the discovered type modifier is the same fix ADR-0123 and ADR-0121
  left open, and is tracked as its own bead (SpatialEngine-u2x.49); the
  identity restriction is a pushed `WHERE` over that same column, so it
  follows whatever that bead decides for the predicate.
- **A text identity the store creates itself.** `IngestIdentity.Source` is
  `Int64`-only, so no table this store creates can be keyed on text, and this
  record changes nothing about that. Whether an ingest may name a text field
  as its identity — and what collation such a column would then be declared
  with — is a capability question, not a comparison-semantics one, and is not
  decided here.
- **The attachment sidecar's own identity columns.** `spatial_attachments` is
  the store's own table, keyed on a text `dataset` and a text `feature_id`, and
  its statements compare both the same way — so an attachment of `delta` is
  listed, read, rewritten or deleted for `Delta` on a case-folding database,
  which is this record's defect on a table the store itself creates. It is not
  fixed here: it is a different subject (a provider-owned table, not a
  dataset's identity), and it has a decision this record does not make — a
  sidecar declared under a binary collation, which is the one place that
  option has a consumer, against the per-statement term this record uses, and
  against what a sidecar created by an earlier version keeps. Tracked as its
  own bead, and decided in ADR-0130: the sidecar declares the order (it is the
  store's own table, so this is the exception §3's authored-column rule could
  not reach) and a sidecar created by an earlier version is re-collated on the
  way in.

## Alternatives

- **Refuse a table whose text identity column folds case.** Rejected: the
  answer is defined, and a refusal would deny reads of a table that is
  perfectly readable. The loss is the table's, not the store's to hide.
- **Read the key column's collation from the discovered type modifier and use
  it.** It is the per-column refinement ADR-0123 already left open, and it
  belongs to the bead that owns it (SpatialEngine-u2x.49); taking it here would
  pre-empt that decision for one more statement.
- **Create the store's own text columns under a binary collation.** It has no
  consumer on this bead: a store-created table cannot be keyed on text at all,
  and a store-created text column is only ever read through a comparison that
  already states its order. It remains the right hygiene for a table the store
  creates, and it is a `CREATE TABLE` decision rather than a comparison one.
- **Compare the identity in managed code after the read.** Correct, and it
  reads the whole table to find one row. Rejected on the grounds ADR-0123
  rejected it for the filter.
- **Fix the fixture with a case-sensitive database instead of a case-sensitive
  column.** A database-level collation is a deployment decision this record
  does not make, and it would not reach an authored table at all.

## References

- Principles 6 (contracts outlive implementations), 10 (stores are providers),
  15 (pushdown optional, semantics-preserving), 17 (small kernel).
- ADR-0028 (parameterised SQL, no client text), ADR-0037 (the edit faces),
  ADR-0038 (the lookup), ADR-0065 §2 (the attachment sidecar), ADR-0074 §3-4
  (the predicate vocabulary and the pushdown surface), ADR-0097 (the identity
  column and the ordinal fallback), ADR-0098 §3 (the ordering and comparison
  rule), ADR-0121 (the byte-order probe, reused here), ADR-0123 (the pushed
  comparison states its order — this record is the same rule for the other half
  of the statement), ADR-0126 is this record.
- `src/Spatial.Stores.PostGIS/Core/PostgisIdentity.cs`,
  `src/Spatial.Stores.PostGIS/Core/PostgisPlanQueries.cs`,
  `src/Spatial.Stores.PostGIS/Data/PostgisQueries.cs`,
  `src/Spatial.Stores.PostGIS/PostgisFeatures.cs`,
  `src/Spatial.Stores.PostGIS/PostgisPlanReader.cs`,
  `src/Spatial.Stores.PostGIS/PostgisWriteOperations.cs`,
  `src/Spatial.Stores.PostGIS/PostgisFeatureDeletes.cs`,
  `src/Spatial.Stores.PostGIS/PostgisAttachmentStore.cs`,
  `src/Spatial.Stores.PostGIS/PostgisStore.cs`,
  `src/Spatial.Stores.SqlServer/Core/SqlServerIdentity.cs`,
  `src/Spatial.Stores.SqlServer/Data/SqlServerQueries.cs`,
  `src/Spatial.Stores.SqlServer/SqlServerWriteOperations.cs`,
  `src/Spatial.Stores.SqlServer/SqlServerFeatureDeletes.cs`,
  `src/Spatial.Stores.SqlServer/SqlServerAttachmentStore.cs`,
  `tests/conformance/Spatial.PredicateConformance/PredicateConformanceSuite.cs`,
  `tests/unit/Spatial.Stores.PostGIS.Tests/PostgisIdentityCollationTests.cs`,
  `tests/unit/Spatial.Stores.SqlServer.Tests/SqlServerIdentityCollationTests.cs`,
  `tests/integration/Spatial.PostGIS.Tests/PostgisIdentityCollationIntegrationTests.cs`,
  `tests/integration/Spatial.SqlServer.Tests/SqlServerIdentityCollationIntegrationTests.cs`,
  `tests/integration/Spatial.SqlServer.Tests/SqlServerPredicateConformanceTests.cs`.
