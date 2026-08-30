# ADR-0007: Capability contracts are versioned independently

Status: Accepted

## Context

Capabilities (geometry ops, scans, writes) evolve at different rates. A
single global version forces all plugins to upgrade together.

## Decision

Every capability has a stable identifier and its own version
(`spatial.geometry.buffer@1`). Each version defines purpose, input/output
schemas, error variants, permissions, side effects, streaming/cancellation
behaviour and conformance examples.

## Consequences

- Multiple plugin versions can serve different capability versions side by side.
- Callers request a contract version; resolution picks a compatible provider.
- Contract changes are explicit, documented and independently testable.