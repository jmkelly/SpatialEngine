# Spatial.PluginSdk

Public capability **contracts** and SDK types for plugin developers and
clients. Contracts ship here; the runtime (Spatial.Runtime) routes against
them; plugin implementations install into hosts. Referenced by
Spatial.Runtime only — implementations are never linked in
(`Runtime_references_no_concrete_plugin`, ADR-0006).

## Owned here (Phase 3 — see architecture/capability-model.md)

`CapabilityId`, `ProviderId`, `Permission`, `CapabilityTraits`,
`SchemaDescriptor`, `ErrorVariant`, `ConformanceExample`,
`CapabilityDescriptor`, `CapabilityErrorKind`, `CapabilityError`,
`CapabilityResult` (`CapabilitySuccess`/`CapabilityFailure`),
`ProgressReport`, `CapabilityInvocation`, `ICapabilityProvider`,
`CapabilityProviderBase`.

## Rules

- Public contracts carry only `Spatial.Core` types (ADR-0005); identifiers
  are `dotted.lowercase.name@positive-version`.
- Constructors validate; the registry enforces cross-cutting rules
  (purpose, error variants, traits coherence, duplicates).
- No packages. New contract behaviour lands with SDK + runtime + tests +
  ADR together; versions live in `Directory.Packages.props`. NuGet creation
  is a later milestone — do not add packing metadata yet.