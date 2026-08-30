# Capability Model

Read when defining, registering, resolving or invoking capabilities. See
implementation-plan.md §9 and ADR-0002/0003/0007/0008.

## Shape

A capability is a stable, versioned identifier (e.g.
`spatial.geometry.buffer@1`, `spatial.feature.scan@1`) with:

- purpose
- input and output schemas
- error variants
- required permissions
- side effects
- streaming and cancellation behaviour
- provenance fields
- conformance examples

## Resolution order (deterministic)

1. Explicit provider requested by the caller
2. Compatible resource-local provider
3. Configured preferred provider
4. First healthy provider by stable provider ID

## Rules

- Providers may push down work but must pass the same conformance fixtures.
- Small, focused contracts beat one large `ISpatialDataStore` (ADR-0003).
- Long-running capabilities run as jobs, always cancellable (ADR-0008).
- Resolution and invocation live in `Spatial.Runtime`; implementations are
  never referenced there (architecture tests).