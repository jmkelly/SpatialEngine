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
  coordinate-layout metadata.
- Versioned feature batches.
- JSON exists for debugging and public API usability only; the runtime never
  routes geometry through JSON between workers.
- Worker boundaries are language-neutral (ADR-0013): no NetTopologySuite,
  Npgsql, EF, ASP.NET, Tauri/Rust or renderer types cross them.

## Handles and streams

- Handles are runtime-owned resources with leases; a client failing to
  release them is a leak test case.
- Streams honour backpressure and cancellation; long streams are jobs with
  progress.

Encoding/decoding is core behaviour (canonical round trips are tested in
Phase 1).