# Handoff — Spatial Engine

> Written for the next agent taking over. Read `AGENTS.md`, then
> `architecture/implementation-plan.md` (the source of truth). Phase 4
> (resources, streams and jobs) is complete; Phase 5 (native plugin
> packaging and isolation) is next.

## Repository state

- **Branch:** `main`. Working tree clean at handoff.
- **Phase 4 commits:** `80e211b` (resources/streams/jobs implementation),
  `f433e22` (quality-loop fixes: CRAP/metrics gates, deadline attribution),
  plus this phase's docs/plan/handoff commit.
- **Solution:** `SpatialEngine.slnx`. Tests: `tests/unit/Spatial.Runtime.Tests`
  (197 tests now), `Spatial.Core.Tests` (308), architecture (8), host (3).
- **Verification:** `./eng/verify.sh` (format check + build + full test run)
  passes from this tree.
- The quality-loop audit artifacts (`*.queue.md`, `*.report.json`,
  `coverage-history.csv`, `StrykerOutput/`) are gitignored and untracked —
  do not commit them.

## Completed

### Phase 0–3 — guardrails, core geometry, features, capability runtime

Unchanged; see the previous handoff in git history (`git show
a69efe4:HANDOFF.md`).

### Phase 4 — resources, streams and jobs (`80e211b`, `f433e22`)

All in `Spatial.PluginSdk` (contracts) / `Spatial.Runtime` (behaviour); see
`architecture/interchange.md`, `architecture/capability-model.md`,
`architecture/decisions/ADR-0022/0023/0024`.

- **Resources (ADR-0022)**: `ResourceId`/`ResourceKind`/`ResourceHandle`/
  `ResourceLease`/`ResourceState`/`ICapabilityResource`/`IResourceFactory`
  (SDK) and `ResourceRegistry` (runtime): mints opaque handles owned by a
  provider, tracks open/leased/closed, issues/renews/releases time-bounded
  leases, closes on disposal, and `DisposeOwnerAsync(owner)` reclaims +
  reports leaks when an owner is torn down. Inject a clock
  (`Func<DateTimeOffset>`) for deterministic lease-expiry tests.
- **Streams (ADR-0023)**: `IStreamFactory`/`StreamChannel`/`IStreamWriter`/
  `ICapabilityStream`/`StreamCompletion` (SDK) and `BoundedStream` (runtime,
  `System.Threading.Channels` — in the shared framework, no package).
  Backpressure = full buffer makes `WriteAsync` wait; `TryWrite` probes;
  reads/writes honour cancellation; `Streaming`-trait success values must be
  stream-backed handles (`StreamingContractValidator` — both directions:
  streaming⇒stream, non-streaming⇒not-a-stream, failures pass through).
- **Jobs (ADR-0024)**: `JobId`/`JobState`/`JobEventKind`/`JobEvent`/`IJob :
  IInvocationContext` (SDK) and `JobRegistry`/`CapabilityJob`/`JobRunner`/
  `JobLauncher` (runtime). `CapabilityRuntime.InvokeAsync` routes
  `LongRunning` capabilities through a job internally (outcome gains
  `Provenance.JobId`); `StartJob` returns the handle immediately; pre-check
  failures yield terminal jobs (every request has a job id). States
  `Pending→Running→Completed/Failed/Cancelled/TimedOut`; events are
  append-only and reuse `ProgressReport`/`ICapabilityError`/`ResourceHandle`;
  jobs are always cancellable and timeout via the deadline.
- **Facilities**: `IInvocationContext.Facilities` (`ICapabilityFacilities`)
  is null outside the runtime; the runtime attaches it to every routed
  invocation (inline and job). During a job, `Streams.Create` *publishes*
  the handle as a `JobEventKind.Resource` event so clients can read the
  stream while the job runs.
- **Resource-local resolution step 2 became real**: `InvocationOptions`
  gained `Resource`; the resolver prefers the resource's owning provider.
- **Fixtures**: `ExampleFeatureProvider` gained `spatial.fixture.mint@1`,
  `peek@1`, `stream@1`, `jobstream@1`; `StubProvider` gained a `traits`
  ctor param; `InMemoryComponentHost` exposes `Resources`/`Jobs`.

## Quality gates (Phase 4, all green)

Quality-loop run (with `--skip stryker`): iteration 1, all four gates
green — CRAP 0/1053 methods (floor < 10), authored branch coverage 90.1%
(floor 70%), metrics 0 findings, warnings 0.

**Stryker (my call, this phase):** ran against **Spatial.Runtime** via the
new `tests/unit/Spatial.Runtime.Tests/stryker-config.json` (the loop's own
stryker still targets Core — see gotchas). Score **76.52%** (killed 244,
survived 59, no-coverage 22, timeout 20; break-at 60 → green). Survivors
are mostly diagnostic-string/statement mutants in `BoundedStream`,
`ResourceRegistry`, `CapabilityJob`, `CapabilityResolver` + the
`AttributeDeadlineCancellation`/`TerminalFor` forks. Core was NOT
re-mutated (unchanged since Phase 3: 93.09%, killed 976, survived 33,
no-coverage 49).

## Hard-won gotchas (read before touching this code)

- **codemetrics `hub` gate is findings==0**: ANY finding (even `low`
  severity) turns the metrics gate red. Phase 4's addition of
  `ResourceRegistry`/`JobRegistry` to `CapabilityRuntime` pushed its
  in-repo coupling to 28; fixed by splitting the facade into
  `InlineInvocation` + `JobLauncher` + `PermissionGate` (runtime coupling
  now < 20, 0 findings). Keep facades thin — delegate mechanics.
- **Do not add branches to `CapabilityResolver.Resolve`**: it sits at the
  CRAP boundary (cx must stay ≤ 9); the resource-local preference is
  implemented via the `LocalProvider`/`MatchOrNull`/`LocalHint` helpers —
  keep it that way.
- **`CancelAfter` can fire a few ms early** (timer coalescing), so the
  invoker's `UtcNow >= due` check mislabels a deadline cancellation as a
  caller cancel. `JobRunner.AttributeDeadlineCancellation` re-attributes:
  cancelled + deadline exists + caller token quiet + job token quiet ⇒
  deadline (TimedOut). The *inline* path keeps the Phase 3 wall-clock
  mapping. If you change the job timeout path, keep this attribution.
- **Streaming fixture emission is fire-and-forget**: `spatial.fixture.stream@1`
  writes chunks on a background task with `CancellationToken.None` (so the
  inline invocation's disposed deadline CTS does not kill it); cancel a
  stream by closing the resource. `jobstream@1` writes inside the job and
  completes the stream with `CapabilityError.Cancelled` on cancellation.
- **Backpressure tests need FIFO drains, not fixed batch reads**:
  `ReadBatchAsync(n)` returns *up to* n items — a race. Test helpers
  `ReadExactlyAsync`/`DrainAsync` in `StreamTests` loop until consumed.
- **Duration-check-then-mutate**: `ResourceRegistry.TryRenewLease` validates
  the grant duration BEFORE removing the old lease from the list (a throw
  mid-mutation previously discarded the lease).
- **Stryker the loop runs covers only Core**: `stryker-audit.py` picks the
  test project in sorted order and Core's `stryker-config.json` wins, so
  the loop's stryker mutates Core only. The Runtime config exists for
  manual runs: `cd tests/unit/Spatial.Runtime.Tests && dotnet-stryker
  --config-file stryker-config.json` (~3 min). If a later phase wants the
  *loop* to mutate Runtime, Core's config placement/removal is a policy
  decision.
- Old Phase 3 gotchas still apply: `Assert.NotNull` binds to the void
  overload (use `Assert.IsType`), registration errors are exceptions while
  invocation errors are structured `CapabilityFailure`s, cc ≤ 9 per method,
  keep a final newline in every file, `dotnet format` fixes the rest.

## Next up — Phase 5: Native Plugin Packaging and Isolation

From the plan (§16, Epic F, `architecture/plugin-lifecycle.md`,
`interchange.md`, ADR-0006/0013 — separate-process .NET workers over
language-neutral contracts):

1. Finalise the plugin manifest schema (id, version, capabilities, runtime
   hints) and its validation.
2. Implement separate-process .NET workers: `Spatial.PluginHost.DotNet`
   scaffolding already exists — it currently has no sources (empty project).
3. Add supervision: discovery, manifest validation, health checks,
   restart, side-by-side activation, draining and rollback (plan §10.4
   lifecycle `Discovered -> Validated -> Starting -> Healthy -> Active
   -> Draining -> Stopped`).
4. Add crash, timeout and cancellation fault fixtures.
5. Wire resource/job ids across the worker boundary: the Phase 4
   `ResourceId`/`JobId` value types were designed to be passed as opaque
   tokens; `CapabilityRegistry` is wired by the host, and the `ResourceRegistry`
   `DisposeOwner` hook is the draining call sites.

The in-memory component host remains the conformance test vehicle; Phase 5
adds a child-process host. Watch the `hub`/coupling metrics when wiring
supervision types into the host, and consider updating the `stryker-config`
policy if you want the loop to mutate the new behavioural assemblies.