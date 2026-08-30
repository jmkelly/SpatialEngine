# ADR-0025: Worker boundaries speak versioned line-delimited JSON with runtime-owned facilities

Status: Accepted

## Context

Phase 5 ships the first separate-process plugin workers (ADR-0006/0013).
The boundary needs a protocol: a transport, a framing, a value encoding and
a way for workers to use runtime-owned resources and streams (ADR-0022/0023)
without owning them. gRPC was the original candidate ("local gRPC or
equivalent language-neutral protocol", plan §10.2), but the first worker is
a stdio child process — a local named pipe — where a full RPC stack adds
machinery without adding isolation or safety.

## Decision

- The worker boundary is the worker's stdin/stdout. One message per line,
  each a JSON object with a versioned envelope
  `{"protocol":"spatial.worker/1","type":…,"id":…,"payload":…}`. Lines are
  bounded (4 MB default).
- Messages: hello, ping/pong, invoke, progress, result, cancel, close/closed,
  error, and the facility RPCs facility.mint, facility.stream.create/write/
  complete, facility.result.
- **Inline values** are JSON scalars plus explicit tags: 64-bit integers as
  `$i64` decimal strings (JSON numbers are lossy), bytes as `$bytes` base64,
  and opaque resource handles as `$resource` tokens.
- **Spatial values (geometries, feature batches) are rejected by the codec**
  with a structured error: they cross the boundary as canonical binary
  interchange (ADR-0020), which the operation/store plugins add in later
  phases. The runtime therefore never routes geometry through JSON between
  workers.
- Resources and streams are runtime-owned across the boundary: a worker asks
  the supervisor to mint or write through facility RPCs, and the host's
  `ResourceRegistry`/`BoundedStream` keep the state and the backpressure
  (ADR-0022/0023).
- Supervised lifecycle (plan §10.4): discovery → validation → activation with
  a manifest compatibility check → ping health → active-preference routing →
  drain (wait for in-flight work, reclaim resources via
  `ResourceRegistry.DisposeOwnerAsync`, graceful close) → rollback metadata
  (the still-running previous package). Active preferences are a runtime
  `ActivePreferenceTable` consulted between the resource-local and the
  configured-preference resolution steps.
- A different language implements the same protocol without .NET types
  crossing the boundary (ADR-0013).

## Consequences

- The .NET worker host and the supervisor share one codec and payload shapes;
  non-.NET workers can implement the protocol from
  `architecture/worker-protocol.md`.
- The inline protocol deliberately does not carry spatial values; plugins
  that process geometry must use the canonical binary interchange (Phase 6+)
  — this keeps the "no geometry through JSON between workers" rule honest.
- Backpressure and handle leases are cross-process for real: a stream write
  from a worker blocks until the host's consumer reads; a resource handle
  returned by a worker is a host-registered handle.
- The protocol is versioned (`spatial.worker/1`); changing a message shape is
  a version bump, never a silent edit.