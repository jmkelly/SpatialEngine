# Spatial.PluginSdk

Public capability **contracts** and SDK types for plugin developers and
clients. Contracts ship here; the runtime (Spatial.Runtime) routes against
them; plugin implementations install into hosts. Referenced by
Spatial.Runtime only — implementations are never linked in
(`Runtime_references_no_concrete_plugin`, ADR-0006).

## Owned here

### Capabilities (Phase 3 — see architecture/distilled/runtime.md)

`CapabilityId`, `ProviderId`, `Permission`, `CapabilityTraits`,
`SchemaDescriptor`, `ErrorVariant`, `ConformanceExample`,
`CapabilityDescriptor`, `CapabilityErrorKind`, `CapabilityError`,
`CapabilityResult` (`CapabilitySuccess`/`CapabilityFailure`),
`ProgressReport`, `CapabilityInvocation`, `ICapabilityFacilities` (the
provider-side minting surfaces on an invocation), and the behavioral
abstractions `ICapabilityCatalog`, `ICapabilityProvider`,
`IInvocationContext`, `IPermissionEvaluator`, `ICapabilityError`, and
`CapabilityProviderBase`.

### Resources (Phase 4 — ADR-0022)

`ResourceId`, `ResourceKind`, `ResourceHandle`, `ResourceLease`,
`ResourceState`, `ICapabilityResource`, `IResourceFactory`.

### Streams (Phase 4 — ADR-0023)

`IStreamFactory`, `StreamChannel`, `IStreamWriter`, `ICapabilityStream`,
`StreamCompletion`.

### Jobs (Phase 4 — ADR-0024)

`JobId`, `JobState`, `JobEventKind`, `JobEvent`, `IJob` (which extends
`IInvocationContext`).

### Operations (Phase 6 — ADR-0026)

`BufferContract`, `IntersectionContract`, `ValidateContract` and
`SimplifyContract` declare the versioned geometry operation capabilities
(`spatial.geometry.buffer@1`, `spatial.geometry.intersection@1`,
`spatial.geometry.validate@1`, `spatial.geometry.simplify@1`): capability
ids, interchange schema names, error variants, traits and the shared
`GeometryOperationConformanceExamples` (plan §9 "conformance examples", §18
conformance tests). `GeometryOperationArguments` holds the stable argument
names providers read and the conformance suite builds from. Geometry
arguments and results cross boundaries as canonical binary interchange
(ADR-0020); values in examples are core types only (ADR-0005).

## Rules

- Public contracts carry only `Spatial.Core` types (ADR-0005); identifiers
  are `dotted.lowercase.name@positive-version`.
- Constructors validate; the registry enforces cross-cutting rules
  (purpose, error variants, traits coherence, duplicates).
- Job events reuse the Phase 3 surfaces (`ProgressReport`,
  `ICapabilityError`) and published `ResourceHandle`s — no parallel
  hierarchies.
- No packages. New contract behaviour lands with SDK + runtime + tests +
  ADR together; versions live in `Directory.Packages.props`. NuGet creation
  is a later milestone — do not add packing metadata yet.