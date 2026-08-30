# ADR-0020: Canonical binary interchange is required

Status: Accepted

## Context

Geometry and feature batches cross host, worker, provider and stream
boundaries continuously. JSON is debuggable but too slow and lossy for heavy
data movement.

## Decision

Canonical binary interchange (WKB/EWKB-derived geometry wrapped with CRS and
layout metadata, plus versioned feature batches) is the required wire format
between components. JSON remains for debugging and public API usability;
opaque handles carry expensive or long-lived resources. The runtime never
requires geometry to pass through JSON between workers.

## Consequences

- Large geometry processing stays fast across process boundaries.
- Streams carry feature batches and tiles with backpressure.
- Encoding is part of the core and covered by round-trip tests.