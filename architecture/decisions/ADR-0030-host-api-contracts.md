# ADR-0030: The HTTP host API shares the SDK's contract shapes and value codec

- **Status:** accepted (Phase 9, Epic H)
- **Date:** 2026-08-31
- **Related:** ADR-0007, ADR-0013, ADR-0018, ADR-0020, ADR-0025, ADR-0028

## Context

Phase 9 adds the public HTTP API of the independently executable host and
two client SDKs (TypeScript for the browser workbench, .NET for automation).
Three surfaces need to agree on every wire shape: the host, the .NET SDK and
the TypeScript SDK. The worker protocol already had a language-neutral value
codec, but it lived inside `Spatial.PluginHost.DotNet.Protocol` (the
supervisor machinery) — not reachable from the SDK or the HTTP layer. The
plan's deployment-profiles invariant says "one public API, contract set,
TypeScript SDK and React application in every profile": a single source of
truth for the contracts is required, not three copies that drift.

## Decision

1. **The inline value codec graduates to the SDK.** `WorkerValueCodec` moves
   from `Spatial.PluginHost.DotNet.Protocol` to
   `Spatial.PluginSdk.Codec.ValueCodec` (exception becomes
   `ValueCodecException`). The worker wire protocol and the HTTP host API
   carry argument/result values with the same encoding, so a value never
   changes shape between boundaries: scalars, `$i64`, `$bytes`, `$geometry`
   (canonical SGEOM, ADR-0020), `$crs`, `$resource`. Spatial values other
   than geometry remain rejected inline — feature data crosses as bounded
   streams (ADR-0023), never JSON.

2. **The HTTP request/response shapes live in the SDK.**
   `Spatial.PluginSdk.Http` ships the versioned DTOs (`InvocationRequest`,
   `InvocationResponse`, `JobResponse`, `JobEventDto`, `ResourceDto`,
   `CapabilityDetailDto`, `PluginDto`, …) plus the shared `HostApiJson`
   options (camelCase properties, camelCase enum members). The host, the
   .NET client and the OpenAPI document (generated from the same records)
   cannot drift by construction. The TypeScript SDK's wire types are
   generated from the OpenAPI description and drift-checked in `npm test`.

3. **The host never references plugin implementations** (unchanged,
   architecture guard): every provider arrives as an immutable package under
   `Spatial:PackagesRoot` and is activated by the process supervisor.

## Consequences

- One codec, one DTO set, one OpenAPI document: the "one contract set"
  invariant holds across host, worker wire, .NET SDK and TypeScript SDK.
- `Spatial.PluginHost.DotNet.Protocol` keeps its envelope framing and uses the
  SDK codec; the codec's own tests remain in the PluginHost suite (its home
  for wire behaviour) and are exercised again through the HTTP API tests.
- `Spatial.Core.Geometry`'s production fan-in (Ca) is unchanged by the move:
  one production referrer moves from `Spatial.PluginHost.DotNet.Protocol` to
  `Spatial.PluginSdk.Codec` (the Ca < 8 discipline of ADR-0029 continues to
  govern where geometry-touching production code may live).
- New host API surfaces must keep the Ca discipline: the host's API layer
  reads feature batches and resources through the contract faces
  (ADR-0029) without naming `Spatial.Core.Geometry` types directly.