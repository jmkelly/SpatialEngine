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
  (configured preferences), `CapabilityRuntime` (invocation facade),
  `CapabilityResolver` (deterministic resolution + unavailable
  diagnostics), `CapabilityInvoker` (provider-boundary error guard),
  `CapabilityOutcomeFactory` (outcomes + provenance), `ProviderHealth`,
  `ResolutionStep`, `ResolvedProvider`, `InvocationOptions`,
  `InvocationProvenance`, `CapabilityOutcome`. Permission checks run through
  the injected `IPermissionEvaluator` (default: set membership
  `GrantedPermissionsEvaluator`).
- **Tests** (`tests/unit/Spatial.Runtime.Tests/Fixtures`) host the plan's
  **in-memory component host** (`InMemoryComponentHost`) and example
  capability provider (`ExampleFeatureProvider`: feature count, feature
  envelope, sleep fixture). This is the test vehicle for resolution,
  invocation, errors, deadlines, cancellation, progress and permissions, and
  the template for real providers in later phases.

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
   provider can serve; falls through otherwise (Phase 4 derives it from
   resource ownership).
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

Every outcome carries `InvocationProvenance`: capability, provider, the
resolution step, started-at, duration and the deadline. Progress reports
(`ProgressReport`, fraction in [0, 1] or unquantified) flow from provider to
caller through the invocation.

## Rules

- Providers may push down work but must pass the same conformance fixtures.
- Small, focused contracts beat one large `ISpatialDataStore` (ADR-0003).
- Long-running capabilities run as jobs in Phase 4, always cancellable
  (ADR-0008); the `LongRunning` trait is declared now and routed then.
- Resolution and invocation live in `Spatial.Runtime`; implementations are
  never referenced there (architecture tests).
- Permissions carry no meaning by themselves: the runtime checks the caller's
  granted set against the descriptor's declared requirements on every
  invocation; secrets and scoping stay host-managed (security-model.md).