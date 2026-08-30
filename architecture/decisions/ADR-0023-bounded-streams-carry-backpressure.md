# ADR-0023: Bounded streams carry the backpressure

Status: Accepted

## Context

Progressive results — feature batches, tiles — must stream instead of
arriving as one value (plan §8), and a slow consumer must not force a
producer to buffer unboundedly. A streaming capability declared the
`Streaming` trait in Phase 3, but nothing enforced a stream-shaped result.

## Decision

A bounded stream is a registered resource whose payload is a fixed-capacity
pipe of core-typed items (`BoundedStream`). The provider writes through a
write-side surface and returns the stream's handle as the invocation value;
consumers open the read side through the resource registry under an active
lease.

- **Backpressure:** a full buffer makes writes wait until the consumer
  reads (`FullMode = Wait`); `TryWrite` offers a non-blocking probe.
- **Cancellation:** writes and reads honour cancellation tokens; closing
  the backing resource truncates the stream and wakes blocked writers.
- **Contract enforcement:** a `Streaming` capability must return a
  stream-backed handle — a plain value or a non-stream resource is a
  `contract.violation`, and returning a stream without the trait is equally
  a violation.
- **Long streams are jobs:** a long-running streaming invocation runs as a
  job (ADR-0008); the job publishes the stream handle as an event so
  clients read while the job runs, and progress reports become job events.

## Consequences

- Producers cannot overload memory: the bounded buffer is the contract.
- The `Streaming` trait now has teeth; conformance suites can assert the
  result shape.
- Reading always happens under a lease, so streams share the resource
  lifecycle (disposal, leak reclamation).