# Spatial.Runtime

The routing kernel: resolves invocations to capability providers, enforces
permissions, deadlines and cancellation, produces structured outcomes with
provenance, and owns the Phase 4 runtime model — resources (opaque handles,
leases, disposal), bounded streams (backpressure) and jobs (state, events,
timeouts). References only `Spatial.Core` and `Spatial.PluginSdk` — never a
concrete provider or plugin implementation (architecture tests). The host
wires registries and configurations; the runtime serves.

## Owned here

### Capabilities (Phase 3 — see architecture/capability-model.md)

`CapabilityRegistry`, `CapabilityConfiguration`, `CapabilityRuntime` (the
thin routing facade), `CapabilityResolver`, `CapabilityInvoker`,
`CapabilityOutcomeFactory`, `InlineInvocation`, `PermissionGate`,
`StreamingContractValidator`, `ProviderHealth`, `ProviderRegistration`,
`ResolutionStep`, `ResolvedProvider`, `InvocationOptions`,
`InvocationProvenance`, `CapabilityOutcome`,
`CapabilityRegistrationException`.

### Resources (Phase 4 — ADR-0022)

`ResourceRegistry` (mint, lease, renew, release, close, dispose-owner leak
reclamation, stream opening), `CapabilityFacilities` (the runtime's
`ICapabilityFacilities` implementation).

### Streams (Phase 4 — ADR-0023)

`BoundedStream` (the runtime's `IStreamWriter` + `ICapabilityStream`).

### Jobs (Phase 4 — ADR-0024)

`JobRegistry`, `CapabilityJob`, `JobRunner`, `JobLauncher`.

## Rules

- Resolution order is fixed: explicit pin (hard) → resource-local (from
  `InvocationOptions.Resource`'s owner, else `ResourceLocalProvider`) →
  configured preferred → first healthy by stable `ProviderId` (plan §9).
- Registration validates descriptors; long-running capabilities are always
  cancellable (ADR-0008).
- Long-running invocations always run as jobs (ADR-0024): every job reaches
  a terminal state (`Completed`/`Failed`/`Cancelled`/`TimedOut`); the job
  runner links caller + job + deadline tokens and attributes
  deadline-caused cancellation without wall-clock races.
- The `Streaming` trait is enforced (ADR-0023): success values must match
  the declared streaming shape or they become `ContractViolation`.
- Providers never escape with exceptions or null results — convert to
  structured `CapabilityErrorKind` outcomes.
- Resources are runtime-owned (ADR-0022): only the registry mints and
  closes handles; hosts call `DisposeOwner` when unregistering/replacing a
  provider so leaks are reclaimed and reported.
- Not thread-safe for registrations: register while wiring the host, then
  serve (registry and job/stream access are individually thread-safe).