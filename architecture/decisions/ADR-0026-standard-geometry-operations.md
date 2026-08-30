# ADR-0026: Standard geometry operations are versioned capability contracts

Status: Accepted

## Context

Phase 6 ships the first real spatial operation plugin (Epic G,
`Spatial.Operations.NetTopologySuite`). The operations — buffer, intersection,
validation and simplification — must be replaceable (ADR-0002), versioned
(ADR-0007) and implementable by any provider without leaking third-party
types (ADR-0005). Their declarations need to live with the other capability
contracts so the runtime, the conformance suite and every provider share one
definition.

## Decision

- Four versioned capability contracts ship in `Spatial.PluginSdk.Operations`:
  `spatial.geometry.buffer@1`, `spatial.geometry.intersection@1`,
  `spatial.geometry.validate@1` and `spatial.geometry.simplify@1`. Each is a
  static contract class carrying the `CapabilityId`, the input/output schema
  names, the error variants, the traits and the shared conformance examples
  (`GeometryOperationConformanceExamples`).
- All four operations are **inline, cancellable, pure** capabilities
  (ADR-0008: only long-running operations must be jobs). The result of a
  validation is a successful boolean — an invalid geometry is a `false`
  result, never a failure.
- Geometry arguments and results cross boundaries as canonical binary
  interchange (ADR-0020) via a `$geometry` inline wire tag carrying the
  SGEOM encoding; they never travel as JSON geometry.
- Providers register the contract descriptors verbatim and read arguments by
  the shared names in `GeometryOperationArguments`. A single error variant,
  `invalid.arguments`, covers missing/mistyped/out-of-range arguments and
  input geometries the algorithm cannot process; anything else is a
  provider failure.
- Every provider of a contract must pass the shared conformance suite
  (tests/conformance), which runs the contract's examples — success, empty
  input, unsupported input, cancellation and diagnostics — and checks the
  invocation provenance.

## Consequences

- Contracts outlive implementations: NTS can be replaced behind the same
  identifiers without touching the runtime or SDKS.
- Conformance is data-driven from the contracts themselves; a new provider
  proves itself by passing the same examples.
- The `$geometry` wire tag extends the Phase 5 inline codec (ADR-0025)
  without weakening its explicit-tag rule; feature batches still cross only
  as streams.