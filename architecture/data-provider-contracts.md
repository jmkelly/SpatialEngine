# Data Provider Contracts (PostGIS)

Read when implementing, replacing or conformance-testing a data provider.
Part of Phase 8 (plan §16); implements ADR-0003/0005/0007/0020/0022/0023/
0025/0028.

## The nine contracts

The data-provider capabilities ship as versioned contracts in
`Spatial.PluginSdk.Providers` — replaceable, provider-agnostic declarations
(ADR-0002/0007). PostGIS is the first implementation (`postgis@1`, ADR-0010).

| Contract | Id | Input | Output | Behaviour |
| --- | --- | --- | --- | --- |
| Catalogue list | `spatial.catalogue.list@1` | `catalogue.filter` — optional `pattern` (LIKE wildcards) | `catalogue.metadata` — bounded stream of JSON items | One metadata item per spatial dataset (id, schema, table, geometry column, SRID, row estimate) |
| Dataset describe | `spatial.dataset.describe@1` | `dataset.identity` — `dataset` | `dataset.description` — bounded stream, one JSON item | Schema fields in column order, geometry column with SRID/type, row estimate, feature-identity columns |
| Dataset create | `spatial.dataset.create@1` | `dataset.definition` — `dataset`, `batch` (canonical feature batch bytes), optional `srid` | `dataset.identity` — the created id | Creates a table whose columns come from the batch's schema; geometry fields become a PostGIS geometry column at the SRID |
| Feature scan | `spatial.feature.scan@1` | `dataset.identity` | `feature.batch` — bounded stream of canonical batches | Streams every feature; requires `spatial.feature.read` |
| Feature query | `spatial.feature.query@1` | `feature.query` — `dataset`, optional bbox `minx/miny/maxx/maxy`, optional `filter` | `feature.batch` — bounded stream of canonical batches | Bounding-box and/or parameterised attribute filtering; requires `spatial.feature.read` |
| Feature write | `spatial.feature.write@1` | `feature.write` — `dataset`, `batch`, optional `transaction` | `feature.count` — appended count | Single-transaction append, optionally enlisted in an open transaction; requires `spatial.feature.write` |
| Transaction begin | `spatial.transaction.begin@1` | `none` | `transaction.handle` | Opens a provider-side transaction; returns a runtime-owned handle |
| Transaction commit | `spatial.transaction.commit@1` | `transaction.handle` | `boolean` | Commits an open transaction and ends its handle |
| Transaction rollback | `spatial.transaction.rollback@1` | `transaction.handle` | `boolean` | Rolls back an open transaction and ends its handle |

The scanning/querying/listing capabilities are cancellable streams (never
`LongRunning` by default — ADR-0008 applies if an implementation routes them
through jobs); writes and transactions are cancellable side effects.

## Interchange

- **Feature data is canonical binary, always** (ADR-0020): scan/query stream
  items and write/create batch arguments are `FeatureBatchCodec` v1 byte
  arrays — one stream item per batch, in scan order. Providers encode on the
  in-process path exactly as on the worker path, so conformance observes
  identical shapes. Clients decode with `FeatureBatchCodec.Decode`.
- **Metadata is JSON text items** (the plan's "JSON for debugging and public
  API usability" rule): tiny, human-debuggable documents for catalogue
  entries (`catalogue.metadata`) and dataset descriptions
  (`dataset.description`). The documents are defined in one shared place,
  `DatasetMetadataJson` (`Spatial.PluginSdk.Providers`); field kinds use the
  stable `AttributeKind` member names. Metadata never carries feature or
  geometry payloads.
- **Handles**: a transaction is a runtime-owned resource (kind
  `transaction`, ADR-0022/0025) minted through the invocation facilities and
  returned as the begin result; commit/rollback and enlisted writes accept
  the same handle. After commit/rollback (or a provider restart) the handle
  is inactive and using it is `invalid.arguments` naming the dead
  transaction.
- Dataset identifiers use a strict `schema.table` grammar (each part a
  lowercase `[a-z_][a-z0-9_]*` identifier; `public` is the default schema).
  Identifiers are never concatenated raw into SQL — they are validated
  against that grammar and the discovered catalogue.

## The PostGIS adapter (`Spatial.Provider.PostGIS`)

The reference implementation is the PostGIS plugin (provider `postgis@1`)
on Npgsql 10. Its private adapters (ADR-0005) cover:

- **Geometry interchange — the plugin's only `Spatial.Core.Geometry`
  surface** (ADR-0028, fan-in budget): PostGIS EWKB ↔ core geometry. Reading
  maps EWKB types/flags to core types/layouts (SRID flag → `EPSG:<srid>`
  CRS identity; SRID 0 → unknown CRS; NaN-sentinel empty points stay
  empty), and writing emits little-endian EWKB with the SRID flag from the
  geometry's CRS (non-EPSG authorities are invalid arguments — PostGIS SRIDs
  are integers).
- **Schema discovery**: `geometry_columns` joins
  `information_schema.columns` for field order/kinds/nullability, `pg_class`
  for the row estimate, and primary-key constraints for the feature-identity
  columns. Attribute kinds map from PostgreSQL types: `bool` → Boolean,
  `int2/int4/int8` → Int64, `float4/float8/numeric` → Double, text kinds →
  String, `geometry` → Geometry, `timestamptz/timestamp/date` →
  DateTimeOffset, `uuid` → Guid. Unsupported column types fail with an
  actionable diagnostic naming the column and type.
- **Filter language** (`feature.query`): comparisons `= != <> < <= > >=
  LIKE`, `IS NULL`/`IS NOT NULL`, literals (single-quoted strings with `''`
  escaping, numbers, `true`/`false`), `AND`/`OR` and parentheses. Columns
  must match a discovered field (unknown names are invalid arguments naming
  the available fields); every literal becomes a bound Npgsql parameter —
  nothing client-supplied is concatenated into SQL.
- **Writing**: `ST_GeomFromEWKB(@p, srid)` per geometry attribute, one row
  per feature, all in a single transaction (enlisting in the caller's when a
  transaction handle is supplied); every value is a bound parameter.
- **Streaming**: batches are written through the invocation facilities'
  bounded stream (`feature.stream`) honouring backpressure; cancellation
  fails the stream with `operation.cancelled` (Phase 4 stream-failure
  contract); Npgsql commands run with the invocation token
  (database-command cancellation).

## Secrets and diagnostics — see `architecture/security-model.md`

Connection configuration reaches the provider through the worker process
environment (`SPATIAL_POSTGIS_CONNECTION`) configured by the supervisor at
launch — never through invocations or the web client. Secrets are redacted
from every diagnostic; an unconfigured provider fails with an actionable
`provider.unavailable` message (naming the environment variable, showing no
secret).