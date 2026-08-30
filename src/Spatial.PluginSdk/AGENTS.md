# Spatial.PluginSdk

Public capability **contracts** and SDK types for plugin developers and
clients. Contracts ship here; the runtime (Spatial.Runtime) routes against
them; plugin implementations install into hosts. Referenced by
Spatial.Runtime only — implementations are never linked in
(`Runtime_references_no_concrete_plugin`, ADR-0006).

## Owned here

### Capabilities (Phase 3 — see architecture/capability-model.md)

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