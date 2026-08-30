# Geometry Operation Contracts

Read when implementing, replacing or conformance-testing a spatial operation
provider. Part of Phase 6 (plan §16, Epic G); implements ADR-0002/0005/0007/
0020/0026.

## The four contracts

The standard geometry operations ship as versioned capability contracts in
`Spatial.PluginSdk.Operations` — replaceable, provider-agnostic declarations
(ADR-0002/0007):

| Contract | Id | Input | Output | Behaviour |
| --- | --- | --- | --- | --- |
| Buffer | `spatial.geometry.buffer@1` | `geometry.operation` — `geometry`, `distance`, optional `quadrantSegments` | `geometry` | OGC buffer; negative distances erode |
| Intersection | `spatial.geometry.intersection@1` | `geometry.pair` — `left`, `right` | `geometry` | Set-theoretic intersection; disjoint inputs succeed with an empty result |
| Validate | `spatial.geometry.validate@1` | `geometry.single` — `geometry` | `validation.flag` (bool) | OGC structural validity; an invalid geometry is a successful `false`, never a failure |
| Simplify | `spatial.geometry.simplify@1` | `geometry.operation` — `geometry`, `tolerance` | `geometry` | Douglas-Peucker at a non-negative tolerance; zero returns the geometry unchanged |

Each contract registers one error variant, `invalid.arguments`, covering
missing/mistyped/out-of-range arguments **and input geometries the algorithm
cannot process** (for example a degenerate ring its underlying engine
rejects). Argument names are stable and shared
(`GeometryOperationArguments`). All four operations are inline, cancellable,
pure capabilities (never `LongRunning`, so never job-routed by default —
ADR-0008 applies when an implementation chooses otherwise).

## Interchange

Geometry arguments and results are core `IGeometry` values; across the worker
boundary they travel as **canonical binary interchange** in the `$geometry`
wire tag (SGEOM encoding, ADR-0020) — never as JSON geometry. Malformed
payloads are rejected by the codec with a byte-accurate error. Non-finite
numbers (NaN/infinity) cannot be represented on the JSON wire at all, so the
`non-finite-distance` input-contract example runs only against in-process
providers; the worker still enforces finiteness on every number that does
arrive.

## Adapter semantics (`Spatial.Operations.NetTopologySuite`)

The reference implementation is the NetTopologySuite plugin (provider
`nts@1`). Its private adapter (`Adapters/GeometryAdapter`, ADR-0005) converts
core geometry to NTS and back:

- Every core shape maps (point, line, polygon with holes, multi-point/line/
  polygon and geometry collections), and every NTS result shape maps back,
  including mixed-dimension overlay results.
- **Ring closure is normalised**: the core model does not require closed
  rings, NTS LinearRings do — an open ring is closed by appending its first
  coordinate before processing.
- **Ordinates and CRS**: the algorithms are planar. Z and M ordinates are
  carried across where the algorithm does not resample (simplify preserves
  Z); computed results (buffer, intersection) are XY. The input geometry's
  CRS identity is attached to the result (intersection: the left input's);
  rings and composite children carry no CRS — the composite result does.
- **Validation pre-checks the OGC ring rules** (a non-empty ring must be
  closed and have at least four coordinates) on the core geometry, because an
  open or undersized ring cannot even be constructed as an NTS LinearRing;
  the remaining validity question is NTS's `IsValidOp`. Rings of line
  strings are never mistaken for rings — only polygon rings are checked.
- **Failure mapping**: `TopologyException`/`ArgumentException` from NTS is an
  input the algorithm cannot process → `invalid.arguments` naming the cause;
  anything else is a provider failure.
- Cancellation is honoured before the synchronous algorithm runs; NTS itself
  has no cancellation hooks.

## Conformance

Every provider of a standard capability must pass the same fixtures (plan
§18). The shared suite (`tests/conformance/Spatial.Conformance.Tests`,
`GeometryOperationConformance`) runs the shared conformance examples —
success, empty input, unsupported input, cancellation and diagnostics — and
asserts the invocation provenance (capability, provider, resolution step,
duration) on every outcome. The examples live **with the conformance suite**
(`GeometryOperationConformanceExamples`, tests/ namespace): they carry
geometry values, and SDK-embedded geometry fixtures would push
`Spatial.Core.Geometry`'s production fan-in past the code-metrics diagnosis
threshold (Ca < 8, ADR-0027/0028) — the same reason ADR-0027 moved the
geometry-carrying transform fixtures out of the SDK. The operation
**descriptors** therefore register no embedded examples (their manifest
compatibility check survives by construction, like transform's). The same
invoker delegate drives the in-process provider and the isolated worker
package, so both must agree on every result shape.