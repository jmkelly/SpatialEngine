---
status: accepted
date: 2026-09-13
deciders: maintainer + agent
summary: SQL Server store provider (`sqlserver@1` on Microsoft.Data.SqlClient): WKB interchange, SRID discovered from data then provider metadata, XY-only writes, containerised tests.
---

# ADR-0073: SQL Server data store provider

## Context

ADR-0033 linked one relational store (PostGIS, ADR-0010/ADR-0028) to prove
the data-provider contracts. Customers also keep spatial data in SQL Server:
`geometry`/`geography` columns are the standard way to hold planar or geodetic
shapes inside an operational database, and the published host already speaks
feature/map services over any store that implements the provider contracts.

A second provider has to answer three questions the PostGIS provider never had
to:

1. **Where is the CRS?** PostGIS stores the SRID in the column typmod, so a
   dataset's CRS is discoverable from an empty table. SQL Server has no such
   typmod: the SRID travels with each *value*.
2. **What happens to Z and M?** `geometry`/`geography` are two-dimensional, so
   ordinates the engine carries would be dropped on write.
3. **What does an uncommitted write look like to another connection?**
   PostgreSQL's MVCC hides it; SQL Server's default READ COMMITTED blocks.

The plugin shape, walls and contract set are unchanged: implementation project,
in-process interfaces, host-managed secrets, redacted diagnostics,
database-command cancellation, canonical batch pages, granular additive faces.

## Decision

1. **A second linked implementation project, `Spatial.Stores.SqlServer`**
   (provider `sqlserver@1`), on Microsoft.Data.SqlClient 5, implementing
   `IDataCatalogue`, `IFeatureStore`, `IFeatureLookup`, `ITransactionStore`
   and the additive `IFeatureEditStore`, `IDatasetIngest` and
   `IFeatureAttachmentStore` faces — the same surface PostGIS exposes, so the
   host can offer either database store on identical routes. `Spatial.Host`
   links it and registers the faces under the store key `sqlserver`; the
   connection string comes from `Spatial:SqlServer:ConnectionString` or
   `SPATIAL_SQLSERVER_CONNECTION` (ADR-0018/ADR-0028 §4), and every diagnostic
   passes through the configuration's redaction.

2. **Identifiers, quoting and filters keep the ADR-0028 grammar with T-SQL
   spelling**: a dataset identifier is strict lowercase `schema.table`
   (implicit `dbo`), the injection barrier for generated statements; a field
   name is discovered data kept verbatim and bracket-quoted, refused only when
   SQL Server cannot carry it (empty, a closing bracket, NUL, past 128 bytes).
   Every literal is a bound parameter; the filter language, its lexer, parser
   and error messages are the same grammar the PostGIS provider exposes, and
   the bounding box becomes
   `[geom].STIntersects(geometry::STGeomFromWKB(@p0, srid)) = 1` with the
   envelope written by the interchange and bound as a parameter.

3. **Geometry interchange is OGC WKB** (`STAsBinary` out,
   `geometry::STGeomFromWKB` in) — the format SQL Server's own spatial
   methods read and write, byte-deterministic little-endian, handling per-child
   byte order and the NaN empty-point sentinel. It is the plugin's single
   `Spatial.Core.Geometry` referrer, as ADR-0028 budgeted for PostGIS.

4. **A dataset's CRS is discovered, never guessed.** SQL Server keeps the SRID
   with the value, so the catalogue and `DescribeAsync` sample the first
   non-null value of the dataset's primary geometry column. A dataset that
   holds none falls back to the SRID recorded in the provider-owned
   `spatial_datasets` sidecar (written by `dataset.create` and by ingest, in
   the same transaction as the table), and finally to 4326. An *authored*
   empty table with no metadata row therefore reports 4326: that is the one
   place the provider guesses, it is documented here and in the query layer,
   and creating or ingesting through the store removes the guess.

5. **Layouts are refused, not flattened.** Only XY geometries are written; a
   Z or M geometry fails `invalid.arguments` naming the field and the layout
   (in writes, in `dataset.create` and at ingest planning). This is a
   deliberate difference from PostGIS, which stores Z and M in a typed column:
   SQL Server cannot, so the engine says so instead of quietly dropping the
   ordinate.

6. **Edits are planned from the edited feature's own schema.** A batch that
   omits a dataset column — the common case for a batch against an ingested
   dataset with a store-assigned key — is a valid add; the statement names
   exactly the columns the batch carries, and `OUTPUT INSERTED.…` returns the
   identity the table assigned. An unassigned `OBJECTID` (ADR-0043) still
   omits identity columns the batch *does* carry. A provider error number maps
   to a contract code through one table (constraint violation and
   already-exists → `invalid.arguments`, missing object → `not.found`), so no
   SqlClient detail crosses the boundary.

7. **Tests are containerised, with a DB-free core.** The store-backed matrix
   (catalogue, schema discovery, scan, bbox and attribute queries, write,
   create, transactions, edit, ingest, attachments, cancellation, redaction)
   runs against a real SQL Server container in
   `tests/integration/Spatial.SqlServer.Tests` via Testcontainers, skipping
   with an explicit reason when no Docker daemon is reachable. The pure
   adapters (WKB, identifier grammar, type mapping, filter language, schema
   discovery, row mapping, ingest plan, generated T-SQL, redaction) are covered
   in `tests/unit/Spatial.Stores.SqlServer.Tests`.

## Consequences

- The host can serve a customer's existing SQL Server spatial data on the same
  feature/map services as PostGIS, with the same contract semantics and the
  same failure codes.
- Two providers now duplicate the provider-independent parts of the PostGIS
  plugin — the filter lexer/parser and the dataset-identifier grammar — because
  ADR-0005/ADR-0033 forbid implementation projects from referencing each
  other. Extracting them into a shared provider-support library is deliberate
  follow-up work behind its own ADR, not something this change smuggles in.
- Z/M data is refused rather than silently truncated on this provider; clients
  that need it must keep that data in a PostGIS dataset.
- The mid-transaction visibility difference is a SQL Server property, not a
  contract change: enlisted writes become durable on commit and never survive a
  rollback, which is what the contract promises. The integration suite asserts
  that, and does not pretend an uncommitted write is observable.
- The provider-owned `spatial_datasets` and `spatial_attachments` sidecars are
  fixed identifiers in the store's own database, like PostGIS's
  `spatial_attachments`: no schema a customer owns is altered.
- The Aspire development profile (ADR-0034) still orchestrates PostGIS and Seq
  only. Running SQL Server locally means pointing `SPATIAL_SQLSERVER_CONNECTION`
  at a database (or adding an Aspire SQL Server resource behind its own
  package decision); the container-based integration suite is what proves the
  provider.
