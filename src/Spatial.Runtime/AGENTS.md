# Spatial.Runtime

The routing kernel: resolves invocations to capability providers, enforces
permissions, deadlines and cancellation, and produces structured outcomes
with provenance. References only `Spatial.Core` and `Spatial.PluginSdk` —
never a concrete provider or plugin implementation (architecture tests).
The host wires registries and configurations; the runtime serves.

## Owned here (Phase 3 — see architecture/capability-model.md)

`CapabilityRegistry`, `CapabilityConfiguration`, `CapabilityRuntime`,
`CapabilityResolver`, `CapabilityInvoker`, `CapabilityOutcomeFactory`,
`ProviderHealth`, `ProviderRegistration`, `ResolutionStep`,
`ResolvedProvider`, `InvocationOptions`, `InvocationProvenance`,
`CapabilityOutcome`, `CapabilityRegistrationException`.

## Rules

- Resolution order is fixed: explicit pin (hard) → resource-local →
  configured preferred → first healthy by stable `ProviderId` (plan §9).
- Registration validates descriptors; long-running capabilities are always
  cancellable (ADR-0008).
- Providers never escape with exceptions or null results — convert to
  structured `CapabilityErrorKind` outcomes.
- Not thread-safe: register while wiring the host, then serve.
- Jobs, streams, resources and supervision land in Phase 4 — do not add them
  here early.