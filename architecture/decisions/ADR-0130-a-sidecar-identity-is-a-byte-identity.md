---
status: accepted
date: 2026-09-30
deciders: maintainer + agent
summary: The attachment sidecar's **own** identity columns declare the byte order — `dataset` and `feature_id` are `COLLATE "C"` on PostGIS and `COLLATE Latin1_General_100_BIN2` on SQL Server, the terms ADR-0123 and ADR-0126 use, so an attachment names its feature in the order the feature names itself. ADR-0126 had to leave a text identity's collation to the *authored* table because `IngestIdentity.Source` is `Int64`-only; the store's own sidecar is the exception it names, a text key on a table the store itself declares. The declaration is **unconditional** on Postgres, unlike every other text comparison the store writes: a column *declaration* is the table's own DDL and costs no per-row comparison, so the store reads no catalog to learn it. A sidecar created by an earlier version is brought forward by an explicit re-collate guarded by each column's recorded collation — one catalog read and no rewrite once the table carries it, and a full rewrite of the two columns and their primary-key index once, in the transaction that re-declares them and rebuilds the key (amends 0126).
amends: ADR-0126
---

# ADR-0130: The attachment sidecar's own identity is a byte identity — declared, not restated

## Context

ADR-0126 made every statement that names a feature by a *dataset's* identity
column state the order a text identity is compared in: `COLLATE "C"` on
Postgres unless the database already compares text by bytes,
`Latin1_General_100_BIN2` on SQL Server. It left one table unmentioned, and
named it as its own bead.

`spatial_attachments` — `dbo`'s on SQL Server, `public`'s on PostGIS — is the
store's **own** table. One row per attachment, keyed on a text `dataset`, a
text `feature_id` and a `bigint` attachment id. Both key columns declare no
collation, so they carry the database's, and the six statements that name the
table — `MaxAttachmentId` (the next-id probe), `ListAttachments`,
`GetAttachment`, `UpdateAttachment`, `DeleteAttachment` and `InsertAttachment` —
compare both of them with that inherited collation. The dataset-level probe
every one of them makes first, `RequireFeatureAsync`, is a *dataset's* identity
and is fixed by ADR-0126; the sidecar's own columns are not.

So on SQL Server's shipped collation — case-insensitive, which is what a
`nvarchar` key column with no declaration of its own carries — an attachment
that belongs to `delta` is listed, read, rewritten and deleted when the store
is asked for `Delta`, and the next-id probe answers with `delta`'s high-water
mark for `Delta`. On Postgres the same table is one `CREATE COLLATION …
deterministic = false` away from folding the same way.

There is a second failure on SQL Server that decides the shape of the fix. A
text key under a case-folding collation **cannot hold both codes at all**:

```
Violation of PRIMARY KEY constraint ... The duplicate key value is (Delta).
```

That is ADR-0126 §3's second finding, and it applies here with a sharper edge:
the sidecar's key is not an authored column the deployment may declare, it is
the store's own `CREATE TABLE`. So a store that only added the collation to
each *statement* — the term ADR-0126 uses everywhere else — would fix the
reads and leave `AddAsync` for `Delta` failing three insert retries deep on a
primary-key violation, against a feature the contract says is a different one.
**The declaration is not an alternative to the per-statement term here; it is
the only thing that can answer the insert.** The table the store creates is
exactly the place ADR-0126 §3 said the requirement falls.

The other half is a sidecar that **already exists**. `IF OBJECT_ID(…) IS NULL
CREATE TABLE` and `CREATE TABLE IF NOT EXISTS` both decline to re-declare a
table that is there, so a create statement alone fixes new deployments and
leaves every existing one folding — a fix that only works on an empty
database is not a fix.

## Decision

**1. The sidecar's identity columns declare the byte order; the statements
inherit it.** `dataset` and `feature_id` are declared `COLLATE "C"` on
PostGIS and `COLLATE Latin1_General_100_BIN2` on SQL Server — the same terms
ADR-0123 and ADR-0126 use, so an attachment names its feature in the order the
feature names itself. This is the one place that option has a consumer: ADR-0126
had to leave a text identity's collation to the *authored* table, because
`IngestIdentity.Source` is `Int64`-only and no table this store creates can be
keyed on text. The sidecar is the exception it names — a text key on a table
the store itself declares.

**2. The declaration is unconditional on Postgres, unlike every other text
comparison the store writes.** ADR-0121's conditional — `COLLATE "C"` unless
the database already compares text by bytes — exists to avoid the cost of a
term that buys nothing, and that cost is a cached catalog read. A column
*declaration* costs nothing: it is not a per-row comparison, it is the table's
own DDL. So the sidecar states the order outright on both providers and the
store reads no catalog to learn it.

**3. A sidecar created by an earlier version is brought forward by an explicit
re-collate, run on the way in.** `RecollateAttachmentIdentity` re-declares
each identity column under the byte-order collation **guarded by that column's
recorded collation** — `pg_attribute.attcollation` joined to `pg_collation` on
Postgres, `sys.columns.collation_name` on SQL Server — so it is one catalog
read and no rewrite once the table carries the declaration, and a full rewrite
of the two columns and their primary-key index once, on the first statement
after the upgrade. This is a real migration, not a documented limitation: no
operator step, no data loss, and the answer is idempotent, so the same version
serving an already-migrated table pays only the read.

On SQL Server the re-collate is **not** a bare `ALTER COLUMN`. SQL Server
refuses to re-collate a column an index depends on, and the sidecar's own
primary key names both identity columns —

```
The object 'PK__spatial___…' is dependent on column 'dataset'.
ALTER TABLE ALTER COLUMN dataset failed because one or more objects access this column.
```

— so the migration **drops the key, re-declares the two columns, and rebuilds
the key**, under the constraint's own name and its own key columns read from
`sys.key_constraints` / `sys.index_columns`, so the sidecar is left with
exactly the key it had. A keyless rebuild of the table would answer the same
problem and is rejected here for the guarantee it gives up: the rows move
through a second copy of the table, and the drop/rebuild never does. The two
are **one transaction** (`BEGIN TRY` / `BEGIN CATCH` with an explicit
`ROLLBACK` and a rethrow, and a transaction the store opens only when the
caller has none), so a rewrite that fails part way — a row the byte order would
duplicate, say — rolls the key back rather than leaving a sidecar with no
uniqueness on its identity. Postgres needs none of this: a collation change is
a per-column type change there, and the key is rebuilt with it.

The re-collate **cannot fail on existing rows, and this is why it is safe to
run it unguarded against whatever is in the table**: the rows it is re-keying
are already unique under a collation that is at least as strict as the one
being applied. A case-folding key rejects two codes that differ only in case
*before* a binary key ever sees them, so every existing primary key is already
distinct under the binary order — the migration can only widen the key space,
never narrow it. (Widening is the point: it is what lets `delta` and `Delta`
both live in one table.)

**4. A feature's attachments are its own, and the loss is defined.** An
attachment of `delta` is not listed, read, rewritten or deleted for `Delta`; a
get of it is `not.found` and a delete of it reports `not.found` per id,
exactly as for an attachment that was never written. `AddAsync`'s probe
answers for the feature it was asked about, so `Delta` starts from id one
whether or not `delta` has rows.

## Consequences

- The sidecar compares its identity by bytes on a case-insensitive SQL Server
  database — the shipped one — and on a Postgres database with a
  non-deterministic collation, for every statement that names it, with no
  per-statement term to drift.
- **An attachment statement can no longer seek the sidecar's primary-key
  index on a database whose identity columns carried a folding collation.**
  The same trade ADR-0121 made for the sort key and ADR-0123 for the filter,
  and answered the same way: the re-collate in §3 rebuilds that index under the
  byte order, so the index and the comparison agree. A deployment that never
  runs a statement after the upgrade keeps the old index *and* the old folding
  behaviour — the statements and the declaration are both the store's, so they
  move together.
- Every attachment operation pays one extra statement: a catalog read of two
  columns. It is not cached, because the sidecar can be re-declared out of
  band by a deployer and a cached answer would then be wrong; it is one round
  trip against an operation that already describes the dataset, probes the
  feature and opens the blob.
- `PostgisAttachmentQueriesTests` and the new
  `SqlServerAttachmentQueriesTests` / `PostgisAttachmentIdentityCollationTests`
  pin the create and re-collate statements in both T-SQL and SQL, including
  that the re-collate names no column the identity does not, that the key comes
  off before the first `ALTER COLUMN` and goes back on after the last, and
  that the drop and the rebuild are one transaction; the attachment
  integration suites on both providers measure a folding sidecar end to end —
  an attachment of `delta` is neither listed nor deleted for `Delta`, `Delta`'s
  next id is one, a sidecar created by an earlier version is forward on the
  first statement that reaches it and still carries the same primary key.

## Not decided

- **An index an operator added over the identity columns.** The re-collate
  rebuilds the sidecar's own primary key, because that is the key the store's
  `CREATE TABLE` declares and the only one it knows how to read back. An index
  an operator added over the same columns is a second object blocking the
  `ALTER`, and the statement answers by failing — the drop and the rebuild roll
  back together, so the sidecar is untouched and the error surfaces as
  `store.unavailable` rather than as a silently dropped constraint.
- **A sidecar an operator has re-declared by hand.** The re-collate brings a
  sidecar *to* the byte-order collation, once; a deployer who later re-declares
  the columns under a linguistic collation of their own gets that back, and
  the store's next statement re-collates it. The store's declaration is not a
  promise about a table it does not own the DDL history of.
- **A column that declares a collation of its own** — the per-column term
  driven by the discovered type modifier, which ADR-0121, ADR-0123 and ADR-0126
  each left open to SpatialEngine-u2x.49. The sidecar does not need it (its
  collation *is* its own declaration, and the store knows it), so that bead's
  answer changes nothing here.
