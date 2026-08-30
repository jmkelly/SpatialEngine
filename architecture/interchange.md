# Interchange

Read when defining wire formats, streams or handles. See
implementation-plan.md §8 and ADR-0020.

## Format rules

- **Inline values** for points, envelopes, options and small geometries.
- **Streams** for feature batches, tiles and progressive results, with
  bounded buffering and backpressure.
- **Opaque handles** for datasets, transactions and intermediate results,
  with ownership, leases and disposal.
- Canonical binary geometry: WKB/EWKB-derived, wrapped with CRS and
  coordinate-layout metadata (version 1 spec: `geometry-model.md`
  "Canonical binary format", implemented by `GeometryCodec`).
- Versioned feature batches.
- JSON exists for debugging and public API usability only; the runtime never
  routes geometry through JSON between workers.
- Worker boundaries are language-neutral (ADR-0013): no NetTopologySuite,
  Npgsql, EF, ASP.NET, Tauri/Rust or renderer types cross them.

## Handles and streams

- Handles are runtime-owned resources with leases; a client failing to
  release them is a leak test case. `ResourceRegistry` (Phase 4,
  ADR-0022) mints opaque `ResourceId`s, tracks open/leased/closed state,
  issues and honours time-bounded leases, closes resources on disposal and
  reclaims — and reports — resources leaked by clients when their owning
  provider is disposed. Providers mint handles through the invocation
  facilities and return the handle as the invocation value.
- Streams honour backpressure and cancellation; long streams are jobs
  with progress. `BoundedStream` (Phase 4, ADR-0023) is a fixed-capacity
  pipe: the provider writes through `IStreamWriter`, the consumer reads
  under a lease through `ICapabilityStream`; a full buffer makes writes
  wait until the consumer reads. The `Streaming` trait is enforced — a
  streaming capability must return a stream-backed handle.
- Long-running invocations run as jobs (Phase 4, ADR-0024): a tracked
  state machine with append-only events, published resources (stream
  handles are readable while the job runs), progress, cancellation and
  timeouts — one job API for polling and subscription.

## Across the worker boundary (Phase 5, ADR-0025)

Workers speak the versioned wire protocol (`architecture/worker-protocol.md`)
over stdin/stdout. Handles and streams stay runtime-owned even when the
provider runs out of process:
- A worker mints a resource or creates a bounded stream through facility
  RPCs (`facility.mint`, `facility.stream.*`); the supervisor's facility
  server performs the work in the host's `ResourceRegistry`, so the handle
  the worker returns is a real host handle and leases/leak reclamation
  work unchanged. Stream writes from a worker block until the host
  consumer reads — backpressure is cross-process.
- Inline values are scalars plus `$i64` (64-bit integers), `$bytes`
  (binary), `$geometry` (canonical binary geometry, Phase 6) and
  `$resource` (opaque handle) tags. Geometry crosses the boundary as
  canonical binary interchange (ADR-0020) in the `$geometry` tag — the
  runtime never routes geometry through JSON between workers. Feature
  batches still cross only as streams.
- Draining a worker version reclaims its resources through
  `ResourceRegistry.DisposeOwnerAsync` (the draining call site) before the
  worker process is stopped.

## Data provider value forms (Phase 8, ADR-0028)

Providers exchange data through the capability contracts in
`Spatial.PluginSdk.Providers` (`architecture/data-provider-contracts.md`):

- **Feature data is canonical binary, both directions** (ADR-0020):
  scan/query stream items are `FeatureBatchCodec` v1 byte arrays (one item
  per batch); write/create batch arguments are the same bytes. The provider
  encodes identically on the in-process and worker paths so conformance
  fixtures see one shape.
- **Metadata is JSON text items**: catalogue entries (`catalogue.metadata`)
  and dataset descriptions (`dataset.description`) are small, debuggable
  JSON documents defined by `DatasetMetadataJson` in the SDK — the plan's
  "JSON for debugging and public API usability" rule, never applied to
  feature or geometry payloads.
- **Transactions are opaque handles** (ADR-0022): `transaction.begin`
  returns a runtime-owned `$resource` handle; commit/rollback and enlisted
  writes carry it back; provider-side state dies with the handle's
  transaction or the worker instance.
- **No new inline tags**: the Phase 5 codec vocabulary is unchanged
  (scalars, `$i64`, `$bytes`, `$geometry`, `$crs`, `$resource`, `ProviderId`);
  stream items are values it already carries.

Encoding/decoding is core behaviour (canonical round trips are tested in
Phase 1).