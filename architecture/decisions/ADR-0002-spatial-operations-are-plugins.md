# ADR-0002: Spatial operations are capability plugins

Status: Accepted

## Context

Operations such as buffer, intersection and validation change often, come
from different providers, and must be replaceable without restarting the
engine. Hard-coding them into the runtime couples the kernel to every
algorithm.

## Decision

Spatial operations are plugins that implement versioned capability contracts
(e.g. `spatial.geometry.buffer@1`). The runtime discovers, resolves and
routes invocations; it never references an operation implementation (enforced
by architecture tests).

## Consequences

- Operations can be replaced side by side and drained independently.
- Compatible providers are interchangeable behind the same contract.
- New operations ship as plugins, not as core changes.