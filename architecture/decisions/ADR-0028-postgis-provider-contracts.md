# ADR-0028: PostGIS provider contracts and data interchange

Status: Accepted

## Context

Phase 8 (plan §16) ships the backing provider ADR-0010 promised: catalogue,
schema discovery, feature scan, filtering, streaming, writing and
transactions against PostGIS, with host-managed connection secrets and
command cancellation. It must follow the repo's established plugin shape —
versioned contracts with shared conformance fixtures (ADR-0007/0026/0027),
no third-party types on public boundaries (ADR-0005), canonical binary
interchange (ADR-0020), runtime-owned handles and bounded streams
(ADR-0022/0023) and isolated worker deployment (ADR-0006/0025) — and it must
stay inside the code-metrics budget for `Spatial.Core.Geometry` fan-in
(plan §16: Ca < 8, architectural-rigidity diagnosis at Ca ≥ 8).

Two structural constraints shape the decision:

1. **The fan-in budget is spent.** Phase 7 left `Spatial.Core.Geometry` at
   Ca 7 — the last green slot was used by the transform runner. The PostGIS
   provider must convert PostGIS rows into core `IGeometry` values (its
   write path goes the other way), which needs exactly one
   geometry-referencing production type. The one deletable existing referrer
   is `Spatial.PluginSdk.Operations.GeometryOperationConformanceExamples` —
   Phase 6's embedded operation fixtures. ADR-0027 already moved
   geometry-carrying fixtures out of the SDK for the same reason; this ADR
   applies the same rule to the Phase 6 examples and reuses the slot for the
   provider's geometry interchange.
2. **The inline wire codec rejects feature batches by design** (ADR-0020:
   "feature batches cross as streams"; worker-protocol.md). Phase 8's data
   paths may not add a feature tag to the codec; feature data crosses as
   canonical binary interchange, and the provider returns streams through
   the Phase 4 `Streaming` trait and `$resource` handles.

## Decision

1. **Nine versioned data-provider contracts ship in
   `Spatial.PluginSdk.Providers`** (ADR-0007), following the §11 vocabulary:

   - `spatial.catalogue.list@1` — optional `pattern`; a bounded stream of
     `catalogue.metadata` JSON items (dataset id, schema/table, geometry
     column, SRID, row estimate).
   - `spatial.dataset.describe@1` — a dataset id; a bounded stream whose one
     item is the `dataset.description` JSON document (fields in column
     order, geometry column with SRID and geometry type, row estimate,
     feature-identity columns).
   - `spatial.dataset.create@1` — a dataset id, a **canonical feature batch**
     (FeatureBatchCodec v1 bytes) whose schema defines the table, and an
     optional SRID (default 4326); side effect, requires
     `spatial.dataset.create`; returns the created id.
   - `spatial.feature.scan@1` — a dataset id; a bounded stream of canonical
     feature batches; requires `spatial.feature.read`.
   - `spatial.feature.query@1` — a dataset id, an optional bounding box
     (`minx`/`miny`/`maxx`/`maxy`, all-or-none, x-first in the dataset's
     coordinate space) and an optional **parameterised attribute filter**
     expression; a bounded stream of canonical feature batches; requires
     `spatial.feature.read`.
   - `spatial.feature.write@1` — a dataset id, a canonical feature batch and
     an optional transaction handle; single-transaction append; requires
     `spatial.feature.write`; returns the appended count.
   - `spatial.transaction.begin@1` / `commit@1` / `rollback@1` — begin
     returns a runtime-owned `transaction` handle (facility-minted,
     ADR-0022/0025); commit/rollback take the handle, end it and make its
     enlisted writes durable / discard them; an inactive handle is an
     invalid argument naming the dead transaction.

2. **Interchange**: feature data — scan/query stream items and write/create
   batch arguments — is **canonical binary** (FeatureBatchCodec v1 bytes,
   ADR-0020): one stream item per batch, encoded by the provider on both the
   in-process and worker paths so the shared conformance fixtures observe
   identical shapes. Metadata (catalogue entries, dataset descriptions) is
   small and human-debuggable, so it crosses as **JSON text items** per the
   plan's "JSON for debugging and public API usability" rule — never feature
   data, never geometry-as-JSON. The `DatasetMetadataJson` shared writer
   (`Spatial.PluginSdk.Providers`) is the single place that defines the
   metadata documents, so every provider and client agree on the shape.
   No new inline codec tags are added: streams ride `$resource` handles and
   their items are `$bytes`/string values the existing codec already carries.

3. **The reference implementation is `Spatial.Provider.PostGIS`
   (provider `postgis@1`)** on Npgsql 10, with a private adapter surface:
   EWKB↔core-geometry interchange (the plugin's **single**
   `Spatial.Core.Geometry` referrer), schema discovery over
   `information_schema`/`geometry_columns`/`pg_class`, row→feature mapping
   with discovered feature-identity columns (primary key values joined with
   `|`, else row ordinals), the filter parser/SQL builder (columns must
   match discovered fields; values are always bound parameters; identifiers
   are validated against a strict `schema.table` grammar), and the
   transaction registry mapping runtime handles to provider-side
   connections/transactions. Geometry columns read as EWKB `byte[]`
   (Npgsql's plugin-free default) and write through
   `ST_GeomFromEWKB(@p, srid)`; SRIDs map to `EPSG:<srid>` CRS identities
   (SRID 0 is an unknown CRS).

4. **Secrets stay host-managed** (ADR-0018, security-model.md): the
   supervisor hands a provider its connection configuration at launch
   through the **worker process environment** (`SPATIAL_POSTGIS_CONNECTION`),
   never through invocations or the client. The worker options gain a
   generic launch-environment dictionary. Connection strings never appear in
   diagnostics: structured errors name a *redacted* form of the
   configuration, and the "no configuration" error is actionable (names the
   environment variable, not a secret).

5. **Cancellation**: every capability honours the invocation token before
   touching the store (pre-cancelled → `operation.cancelled`); Npgsql
   commands run with the token (database-command cancellation); a scan or
   query cancelled mid-stream fails the stream with `operation.cancelled`
   (the Phase 4 stream-failure contract).

6. **Conformance and integration**: the shared conformance suite
   (`tests/conformance`) gains the provider contracts' **DB-free matrix**
   (argument validation, filter grammar errors, unconfigured-provider
   `provider.unavailable` diagnostics with redaction, pre-cancelled
   failures) run in-process **and** against the packaged `postgis@1` worker;
   the store-backed matrix (real catalogue/schema/scan/query/write/
   transaction behaviour) lives in the containerised integration suite
   (`tests/integration/Spatial.PostGIS.Tests`) on a Testcontainers PostGIS
   image. The Phase 6 operation examples moved to the conformance suite
   (tests/) with this change; the operation **contracts** are unchanged
   (same ids, schemas, errors, traits) and the suite runs the same fixtures
   from its new home.

## Consequences

- Any data store can implement the same contracts (GeoParquet, COG, OGC
  APIs later, plan §11) and pass the same conformance fixtures; the provider
  contracts are proven against a real PostGIS from Phase 8 on.
- `Spatial.Core.Geometry` fan-in stays Ca 7: the Phase 6 example relocation
  frees one slot and `PostgisGeometryInterchange` takes it. Npgsql types,
  SQL and EWKB stay inside the plugin (ADR-0005); nothing data-store-related
  crosses a public boundary.
- Feature data flows as canonical binary in both directions (scan/query
  streams out, write/create batches in) — ADR-0020's required wire form —
  with metadata as the documented JSON exception (small, debuggable, API
  facing).
- The worker protocol's message vocabulary is unchanged (no new tags);
  only the supervisor's process environment becomes configurable, which is
  the advertised secret channel for every future provider.
- Redaction and parameterised filters make client input safe by
  construction; the security-model doc is updated with the mechanism.