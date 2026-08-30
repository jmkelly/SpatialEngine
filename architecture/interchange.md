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

Encoding/decoding is core behaviour (canonical round trips are tested in
Phase 1).