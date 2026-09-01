# Runtime: Capabilities, Resources, Streams, Jobs (distilled)

Covers `Spatial.PluginSdk` contracts and `Spatial.Runtime` routing. Implements
ADR-0002/0003/0007/0008/0022/0023/0024.

## Capability shape

A capability = stable versioned id `dotted.lowercase.name@version` +
purpose, input/output schemas, error variants, required permissions,
side effects, streaming/cancellation behaviour, provenance fields,
conformance examples. Provider ids and permission names follow the same
dotted-lowercase rule.

**Registration rules** (violations → `CapabilityRegistrationException`):
duplicate provider id; provider with zero capabilities; duplicate capability
id in one provider; descriptor missing purpose/schema names/error
variants/permissions declaration; a long-running capability that is not
cancellable.

## Resolution order (deterministic)

1. **Explicit provider pin** (caller) — hard; fails `ProviderUnavailable` with reason.
2. **Resource-local provider** — the owner of `InvocationOptions.Resource`'s handle (ADR-0022), else explicit `ResourceLocalProvider`.
3. **Active preferred provider** — supervisor-driven preference table (`CapabilityRuntime.SetActivePreference`); soft, reversible, used for side-by-side replacement routing.
4. **Configured preferred provider** — from host config (`Spatial:Preferences`); soft.
5. **First healthy provider** — ordinal by provider id (name, then version).

`Healthy`/`Degraded` serve; `Unhealthy` doesn't. The winning step is recorded
in provenance.

## Invocation flow (`CapabilityRuntime.InvokeAsync`)

1. Past deadline → `DeadlineExceeded` immediately.
2. Unresolvable → `CapabilityNotFound` (with registered list) or `ProviderUnavailable` (with why).
3. Missing permissions → `PermissionDenied` naming them (checked via injected `IPermissionEvaluator`; default set membership `GrantedPermissionsEvaluator`).
4. Effective token links caller cancellation + deadline.
5. Null results / errorless failures / exceptions → structured `ContractViolation`/`ProviderFailure`; `OperationCanceledException` → `Cancelled` (or `DeadlineExceeded` past deadline).
6. Shape enforcement: `Streaming` capability MUST return a stream-backed handle and non-streaming MUST NOT (else `ContractViolation`).
7. Long-running capabilities route through the job model; `StartJob` returns the handle immediately for polling/subscription. Pre-check failures still yield a tracked job (terminal state).

Every outcome carries `InvocationProvenance`: capability, provider, resolution
step, started-at, duration, deadline, job id (when job-routed). Progress
(`ProgressReport`, fraction [0,1] or unquantified) flows provider → caller and
becomes job events when a job runs.

## Resources (ADR-0022)

- Runtime-owned, opaque `ResourceId` (never reused, meaningless to clients); `ResourceRegistry` tracks open/leased/closed behind the id.
- Leases are time-bounded rights to use; honoured on stream reads; unreleased leases are reclaimed and **reported as leaks** when the owner is disposed (`DisposeOwnerAsync`).
- Providers mint via `invocation.Facilities.Resources.Create(kind)` / `.Streams` and return the handle as the invocation value.
- Passing a handle as `InvocationOptions.Resource` enables resource-local resolution (step 2).

## Streams (ADR-0023)

- `BoundedStream` = fixed-capacity pipe of core-typed items; provider writes via `IStreamWriter`, consumer reads under a lease via `ICapabilityStream`.
- Backpressure: full buffer makes writes **wait** until the consumer reads (`FullMode = Wait`; `TryWrite` for a non-blocking probe). Cross-process too — a worker's stream write blocks on the host consumer.
- Cancellation: reads/writes honour tokens; closing the backing resource truncates and wakes blocked writers.
- The `Streaming` trait has teeth (rule 6 above). Long streams run as jobs that publish the handle as a job event so clients read while the job runs.

## Jobs (ADR-0024)

- State machine `pending → running → completed | failed | cancelled | timedOut`.
- Append-only events: lifecycle (`created/started/completed/failed/cancelled/timedOut`), `progress` (fraction + message), `resource` (published handle), `note` (provider diagnostics).
- `IJob : IInvocationContext` — the job handle is the same read-only surface providers already use.
- Deadline fires → `timedOut` + `deadline.exceeded`, attributed without wall-clock races. One effective token links caller + job + deadline.
- `JobRegistry.Forget` prunes finished jobs.

## Interchange rules (applies everywhere)

- **Inline values**: points, envelopes, options, small values.
- **Streams**: feature batches, tiles, progressive results — bounded, backpressured.
- **Opaque handles**: datasets, transactions, intermediates.
- **Canonical binary** is the required wire format for geometry and feature
  batches (ADR-0020). **JSON exists for debugging and public-API usability
  only** — metadata documents are the sanctioned exception; the runtime never
  routes geometry or feature payloads through JSON between workers.
- Handles and streams stay runtime-owned even across process boundaries
  (workers ask via facility RPCs; see `plugins.md`).
