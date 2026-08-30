# ADR-0006: Isolated worker processes are the default plugin boundary

Status: Accepted

## Context

In-process plugin unloading can be blocked by statics, threads and native
dependencies. Third-party plugins must not endanger the host.

## Decision

Substantial plugins run in separate executable processes over a
language-neutral protocol with crash isolation, independent restart,
resource controls and side-by-side versioning. Collectible
`AssemblyLoadContext` (in-process) and WebAssembly hosting exist only for
controlled, justified cases and are not treated as security boundaries.

## Consequences

- A crashing plugin cannot take down the host.
- Replacement lifecycle (start beside, drain, stop) is executable per plugin.
- Cross-process overhead is mitigated by binary batches, streams and handles.