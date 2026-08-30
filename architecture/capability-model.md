# Capability Model

Read when defining, registering, resolving or invoking capabilities. See
implementation-plan.md §9/§16 (Phase 3) and ADR-0002/0003/0007/0008.

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

Identifiers are `dotted.lowercase.name@positive-version`. Provider ids and
permissions follow the same dotted-lowercase rule (`nts@1`,
`spatial.feature.read`).

## Where the Phase 3 surface lives

- **`src/Spatial.PluginSdk/Capabilities`** ships the contracts: `CapabilityId`,
  `ProviderId`, `Permission`, `CapabilityTraits`, `SchemaDescriptor`,
  `ErrorVariant`, `ConformanceExample`, `CapabilityDescriptor`,
  `CapabilityErrorKind`/`CapabilityError`, `CapabilityResult`,
  `ProgressReport`, `CapabilityInvocation`, `ICapabilityProvider`,
  `CapabilityProviderBase`. The behavioral abstractions providers, the
  runtime and hosts depend on are `ICapabilityCatalog` (declaration surface),
  `ICapabilityProvider` (declaration + invocation), `IInvocationContext`
  (read-only invocation view), `IPermissionEvaluator` (pluggable permission
  policy) and `ICapabilityError` (structured error surface). The SDK
  references only `Spatial.Core`; contracts carry only core types (ADR-0005).
- **`src/Spatial.Runtime/Capabilities`** owns routing: `CapabilityRegistry`
  (registrations + descriptor validation), `CapabilityConfiguration`
  (configured preferences), `CapabilityRuntime` (thin invocation facade),
  `CapabilityResolver` (deterministic resolution + unavailable
  diagnostics), `CapabilityInvoker` (provider-boundary error guard),
  `CapabilityOutcomeFactory` (outcomes + provenance), `InlineInvocation`
  and `PermissionGate` (inline mechanics + shared permission pre-check),
  `StreamingContractValidator` (the Streaming trait has teeth), `ProviderHealth`,
  `ResolutionStep`, `ResolvedProvider`, `InvocationOptions`,
  `InvocationProvenance`, `CapabilityOutcome`. Permission checks run through
  the injected `IPermissionEvaluator` (default: set membership
  `GrantedPermissionsEvaluator`).
- **`src/Spatial.PluginSdk/Resources` + `Spatial.Runtime/Resources`** (Phase 4,
  ADR-0022): `ResourceId`, `ResourceKind`, `ResourceHandle`, `ResourceLease`,
  `ResourceState`, `ICapabilityResource`, `IResourceFactory` and the runtime's
  `ResourceRegistry` — opaque, runtime-owned handles with leases, disposal and
  leak reclamation; `ICapabilityFacilities` on the invocation context carries
  the provider-side minting surfaces.
- **`src/Spatial.PluginSdk/Streams` + `Spatial.Runtime/Streams`** (Phase 4,
  ADR-0023): `IStreamFactory`, `StreamChannel`, `IStreamWriter`,
  `ICapabilityStream`, `StreamCompletion` and the runtime's `BoundedStream` —
  bounded buffering, backpressure and the enforced `Streaming` result shape.
- **`src/Spatial.PluginSdk/Jobs` + `Spatial.Runtime/Jobs`** (Phase 4, ADR-0024):
  `JobId`, `JobState`, `JobEventKind`, `JobEvent`, `IJob : IInvocationContext`
  and the runtime's `JobRegistry`, `CapabilityJob`, `JobRunner`, `JobLauncher`
  — observable state machines with events, cancellation and timeouts for
  long-running invocations.
- **Tests** (`tests/unit/Spatial.Runtime.Tests/Fixtures`) host the plan's
  **in-memory component host** (`InMemoryComponentHost`) and example
  capability provider (`ExampleFeatureProvider`: feature count, feature
  envelope, sleep, and the Phase 4 mint/peek/stream/jobstream fixtures). This
  is the test vehicle for resolution, invocation, errors, deadlines,
  cancellation, progress, permissions, resources, streams and jobs, and the
  template for real providers in later phases.

## Registration rules (enforced by the registry)

A provider registers with its descriptors. Rejected with an actionable
`CapabilityRegistrationException` when:

- the provider id is already registered, or it declares no capabilities;
- a provider declares the same capability id twice;
- a descriptor has no purpose, an unnamed input/output schema, no error
  variants, or no required-permissions declaration;
- a long-running capability is not cancellable (ADR-0008: long-running
  operations are always cancellable).

## Resolution order (deterministic)

1. **Explicit provider requested by the caller** — a *hard* pin. When the
   pinned provider is not registered, does not serve the capability, or is
   unhealthy, resolution fails with `ProviderUnavailable` naming the reason.
2. **Compatible resource-local provider** — preferred when the owning
   provider can serve; falls through otherwise. The preference comes from
   `InvocationOptions.Resource` (the resource's owning provider — derived
   from resource ownership, ADR-0022) or, when absent, the explicit
   `ResourceLocalProvider` option.
3. **Configured preferred provider** — soft; an absent or unhealthy preferred
   provider falls through.
4. **First healthy provider by stable provider ID** — ordinal by `ProviderId`
   (name, then version).

`Healthy` and `Degraded` providers can serve; `Unhealthy` cannot. The step
that won is recorded on the resolved provider and in the invocation
provenance.

## Invocation flow

`CapabilityRuntime.InvokeAsync(invocation, options)` resolves, then:

1. A deadline already in the past fails immediately (`DeadlineExceeded`).
2. Unresolvable → `CapabilityNotFound` (with the registered capability list)
   or `ProviderUnavailable` (with why).
3. Required permissions are checked against the caller's granted set;
   missing ones are named in a `PermissionDenied` error.
4. The effective token links the caller's cancellation with the deadline
   (`CancelAfter`), so both surfaces reach the provider.
5. Provider results pass through; null results, failures without an error and
   thrown exceptions become structured `ContractViolation`/`ProviderFailure`
   outcomes. `OperationCanceledException` becomes `Cancelled` or, when the
   deadline passed, `DeadlineExceeded`.
6. The result must match the *declared* shape: a `Streaming` capability must
   return a stream-backed handle and a non-streaming capability must not
   (ADR-0023); anything else becomes `ContractViolation`.
7. **Long-running capabilities route through the job model** (ADR-0008/
   ADR-0024): `InvokeAsync` creates a job, awaits it and returns its outcome
   with the job id in the provenance; `StartJob` returns the handle
   immediately for polling or subscription. Job state, events (progress via
   `ProgressReport`, failures via `ICapabilityError`, published resources via
   `ResourceHandle`) and timeout/cancellation behaviour are uniform across
   providers. Pre-check failures still yield a tracked job (terminal state).

Every outcome carries `InvocationProvenance`: capability, provider, the
resolution step, started-at, duration, the deadline and — for job-routed
invocations — the job id. Progress reports (`ProgressReport`, fraction in
[0, 1] or unquantified) flow from provider to caller through the invocation
and are recorded as job events when a job runs.

## Rules

- Providers may push down work but must pass the same conformance fixtures.
- Small, focused contracts beat one large `ISpatialDataStore` (ADR-0003).
- Long-running capabilities always run as jobs, always cancellable
  (ADR-0008/ADR-0024); `StartJob` exposes the job API for any invocation.
- Resources are runtime-owned with leases (ADR-0022): a provider mints them
  through `invocation.Facilities.Resources/Streams` and returns the handle;
  the runtime tracks leases, disposal and leak reclamation, and derives the
  resource-local provider from a handle's owner during resolution.
- Streaming results are enforced (ADR-0023): the `Streaming` trait means the
  result IS a stream handle; long streams run as jobs that publish the handle
  and report progress.
- Resolution and invocation live in `Spatial.Runtime`; implementations are
  never referenced there (architecture tests).
- Permissions carry no meaning by themselves: the runtime checks the caller's
  granted set against the descriptor's declared requirements on every
  invocation; secrets and scoping stay host-managed (security-model.md).