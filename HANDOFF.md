# Handoff — Spatial Engine

> Written for the next agent taking over. Read `AGENTS.md`, then
> `architecture/implementation-plan.md` (the source of truth). **Phase 5
> (native plugin packaging and isolation) is complete**; Phase 6 (NetTopologySuite
> operations plugin) is next.

## Repository state

- **Branch:** `main`. Working tree clean at handoff.
- **Phase 5 commits:**
  - `1d3ec38` — plugin manifest schema + validation (+ `architecture/plugin-manifest.md`)
  - `5c1f33a` — worker wire protocol v1 (envelope codec, value codec, channel) (+ `architecture/worker-protocol.md`)
  - `371c887` — worker host executable + `Spatial.Plugin.Fixtures` fault-fixture plugin
  - `84730a6` — supervision (discovery/activation/health/restart/side-by-side/drain/rollback) + worker proxy + runtime active-preferences
  - final commit — quality-loop refactors, docs/ADR-0025/plan ticks/README/HANDOFF, flake fix
- **Tests:** 593 total (Core 308, Runtime 201, PluginHost 73, Architecture 8, Host 3).
  `./eng/verify.sh` passes from this tree.
- Quality-loop artifacts (`*.queue.md`, `*.report.json`, `coverage-history.csv`,
  `StrykerOutput/`) are gitignored — do not commit them.

## Completed

### Phase 0–4 — guardrails, core geometry, features, runtime, resources/streams/jobs

Unchanged; see git history handoffs. Phase 5 builds on the Phase 4
`ResourceId`/`JobId` opaque tokens and the `ResourceRegistry.DisposeOwner` draining hook.

### Phase 5 — native plugin packaging and isolation (Epic F)

All in `Spatial.PluginHost.DotNet` (worker protocol + supervision; ADR-0025)
and `Spatial.Runtime`/`Spatial.PluginSdk` (active preferences, `IResourceHandle`);
see `architecture/plugin-manifest.md`, `architecture/worker-protocol.md`,
`architecture/plugin-lifecycle.md`, `interchange.md`, `capability-model.md`,
ADR-0025 (and the ADR-0022 paragraph for `IResourceHandle`).

- **Manifest schema v1** (`Manifest/`): `PluginManifest`(+ capabilities/errors/
  examples), `PluginManifestValidator` (mirrors registry rules + packaging
  rules), `PluginManifestLoader`, `ManifestTraitMap`, `ManifestCompatibility`
  (activation check between a loaded provider's surface and its manifest),
  `ManifestDescriptorBuilder` (manifest → proxy descriptors).
- **Wire protocol `spatial.worker/1`** (`Protocol/`): envelope
  `{"protocol","type","id","payload"}` over line-delimited JSON; messages
  hello/ping/pong/invoke/progress/result/cancel/close/closed/error +
  facility RPCs (mint, stream.create/write/complete, result). `WorkerChannel`
  is the shared two-way endpoint (request/response correlation, bounded
  lines, disconnect fails all pending). **Inline values**: scalars, `$i64`
  decimal strings, `$bytes` base64, `$resource` handle tags; **spatial values
  are rejected** with an ADR-0020 hint — geometry must use canonical binary
  interchange (needed by Phase 6). "The runtime never routes geometry through
  JSON between workers" is enforced by the codec.
- **Worker host executable** (`WorkerHost/`): `WorkerProgram --package <dir>`;
  `PluginPackageLoader` loads the package in a *collectible* ALC that prefers
  the DEFAULT context for already-loaded identities; `PluginWorkerHost` runs
  the loop (hello → invoke/cancel/ping/close), per-invoke deadline
  cancellation, progress relay, drain. `WireFacilities` mints resources and
  streams through supervisor RPCs (`facility.*`).
- **Supervision** (`Supervision/`): `PluginDiscovery` (validated packages from
  a root), `WorkerProcess` (spawn + stderr capture), `WorkerHealthMonitor`
  (ping loop, failure threshold), `RestartPolicy` (exponential backoff,
  max attempts), `WorkerInstance` (plan §10.4 lifecycle states),
  `WorkerFacilityServer` (host-side mint/stream create/write/complete with a
  per-worker gate), `WorkerInvocationRelay` (in-flight + progress routing),
  `WorkerProvider` (the `ICapabilityProvider` proxy — cancellation forwarded
  as `cancel`, crash → `ProviderFailure`), `InvokeFailureMapper`,
  `WorkerSupervisor` (thin orchestrator: ActivateAsync, DrainAsync,
  RestartAsync, HealthCheckOnceAsync, RunHealthChecksAsync,
  RouteNewWorkTo, RollbackAsync, Discover), `SupervisorEvent` diagnostics.
- **Runtime**: `ActivePreferenceTable` + `ResolutionStep.ActivePreferred`
  (consulted between resource-local and configured preference);
  `CapabilityResolver.Resolve` consolidated to Cx 9; `ResourceRegistry.Create`
  (payload) made public for the facility server.
- **Fixtures**: `tests/fixtures/Spatial.Plugin.Fixtures` — `FixtureProviderV1`
  (peek/mint/sleep/timeout/crash/stream) and `FixtureProviderV2`
  (side-by-side); packaged + spawned as real children by the tests.
- **Tests**: `Spatial.PluginHost.DotNet.Tests` (73): manifest, codec/value,
  channel, worker-process (12 spawn tests), supervisor lifecycle (12 spawn
  tests: activation, duplicate refusal, mint, job cancel/timeout,
  side-by-side routing, drain-with-inflight, crash restart, restart-policy
  exhaustion, streaming, rollback, restart registration). Runtime:
  `ActivePreferenceTests` (4).

## Quality gates (Phase 5 — all green)

Quality-loop (`--skip stryker`) drove two implementor passes; final state:
- **CRAP 0** of 1404 methods (floor < 10).
- **Coverage 78.9% branch** (floor 70%) — *dropped from 90.2%*: the merged
  cobertura now counts child-process (spawned worker) coverage for `src/`
  (~61% there — untouched worker-host error paths), not a regression.
- **Metrics 0 findings** (was 5: WorkerSupervisor god-class → health-monitor
  split; hub WorkerProvider → facade slimmed; architectural-rigidity →
  `IResourceHandle` abstraction; Protocol rigidity → delegate injection).
- **Warnings 0** (clean `--no-incremental` build).

**Stryker (my call):** the loop's stryker targets **Core only** (unchanged this
phase; last scores 93.09% → run-level 87.27%, break-60 green). I attempted a
**targeted Stryker on the new `Spatial.PluginHost.DotNet` assembly** (its
config added to the new test project) — it did NOT complete synchronously:
663 mutants to test, each running the 42s process-level suite. Start of that
run: 307 compile-error mutants (safe-mode noise mutating guard-clause
patterns), 345 no-coverage, 280 ignored-block, 932 skipped / 663 to test. A
background 3-hour run was started at handoff-time; **the new assembly's final
mutation score is unmeasured yet** — the loop's final repo-wide pass should
time-box it (or exclude the slowest process tests). Report honestly: no
survivor list exists because the run never reached the report stage.

## Hard-won gotchas (read before touching this code)

- **The CRAP audit's coverage rematch only matches TOP-LEVEL classes.**
  Nested classes (incl. `record`-nested and `<>c` closures) report ~0%
  coverage forever, so ANY nested class with Cx ≥ 3 fails the CRAP gate
  (CRAP = Cx²·(1−cov)+Cx ≈ Cx²+Cx at cov 0). Keep implementation classes in
  top-level internal files (LineReader → `Protocol/LineReader.cs`,
  PackageLoadContext → `WorkerHost/PackageLoadContext.cs` were moved for
  exactly this reason), or keep nested classes at Cx ≤ 2.
- **Coverlet only flushes a spawned worker's coverage when the child exits
  cleanly.** Worker tests must end with a clean drain epilogue
  (SendAsync(Close) → await Closed → WaitForExit → assert 0) or the
  worker-side branches they cover count as uncovered.
- **`Progress<T>` with no sync context posts asynchronously and can DROP a
  post under load** — count assertions flake. When the test is about the
  *provider's* emission (order/fractions), use the synchronous
  `RecordingProgress` helper (tests/unit/Spatial.Runtime.Tests/Fixtures) — it
  is not gaming; the unit under test is the emitter, not BCL delivery.
- **Facility RPCs must never run on a protocol read-loop thread.** The worker
  host dispatches invokes with `Task.Factory.StartNew(...).Unwrap()` (the
  provider may block synchronously on a facility GetResult); the supervisor
  processes `facility.*` off-loop with a per-worker semaphore (stream writes
  block on cross-process backpressure). Violating this deadlocks the read loop
  that must answer the RPC (painful to debug — the observable symptom is a
  10 s facility timeout then the response echo landing as "unexpected").
- **Plugin ALC must prefer the DEFAULT context** for already-loaded identities
  (Core/PluginSdk/Runtime/framework): loading the package's copies splits type
  identity and yields an opaque `MissingMethodException` deep inside the
  plugin ctor. `PackageLoadContext.Load` tries
  `Default.LoadFromAssemblyName` first.
- **RestartCount increments before the backoff+spawn; `WatchDisconnectAsync`
  transitions to Starting immediately** on a crash, so observers never see
  `Active` while the worker is down (the state machine is the truth).
- **`WorkerProcess.StopAsync` waits for a graceful exit before killing** —
  `HasExited` lags the actual exit and produced false 137 exit codes on
  clean drains.
- **`CapabilityResolver.Resolve` must stay Cx ≤ 9** (old gotcha; the new
  ActivePreferred arm is folded into the `ResolvePreference` helper the loop's
  implementor consolidated — keep it that way).
- **codemetrics interpreted diagnoses (god-class/hub/rigidity) are NOT
  suppressible via `.dependably` exceptions** — they need code changes
  (confirmed against the tool source). Threshold rules are suppressible.
- **`CapabilityRuntime.SetActivePreference/ClearActivePreference`** is the
  supervisor's routing lever (plan §10.4 "route new work to the new
  version"); it is soft and reversible, consulted between resource-local and
  configured preference (resolution step 3).
- **Stryker for this assembly in manual runs is SLOW** (~42 s test cycle ×
  hundreds of mutants); the loop's stryker still picks Core (sorted first
  configured test project). If the loop should mutate PluginHost too, extract
  the slow spawn tests or raise the loop's time budget — policy decision.
- Old gotchas still apply: no branch growth in `CapabilityResolver.Resolve`,
  `Assert.NotNull` binds to the void overload (use `Assert.IsType`), cc ≤ 9
  per method, final newline in every file, `dotnet format` fixes the rest.

## Next up — Phase 6: NetTopologySuite Operations Plugin

From the plan (§16, Epic G): define buffer/intersection/validate/simplify
contracts; implement adapters WITHOUT public NTS types (ADR-0005); shared
conformance fixtures and provenance.

What Phase 5 leaves ready for it:

1. A real operation plugin is a **package**: manifest v1 + an assembly
   implementing `ICapabilityProvider` — the worker host, supervisor and
   lifecycle handle it today (the fixture plugin is the template:
   `tests/fixtures/Spatial.Plugin.Fixtures`).
2. **Geometry cannot cross the worker boundary inline yet** — the codec
   rejects spatial values with an ADR-0020 hint. Phase 6 must add the
   canonical binary interchange path over the wire (WKB geometry via
   `$bytes`/a new dedicated message, feature batches as streams) so
   `spatial.geometry.buffer@1` arguments/results travel as canonical binary,
   NOT JSON. This is the highest-value missing piece.
3. **Streaming across the boundary works** (facility.stream.* + host-side
   `BoundedStream`; backpressure is real); the *job-publish* gap: stream
   handles published on jobs (`JobEventKind.Resource`) while a worker job
   runs are not yet wired (the supervisor's facility server doesn't know the
   job id) — the NTS worker's streaming jobs will need the invoke payload to
   carry the job id or an equivalent hook.
4. **Host wiring is still Phase 9** (plan): `Spatial.Host` is a stub; the
   plan's "CapabilityRegistry is wired by the host" is demonstrated by the
   supervisor+runtime integration tests. Phase 6 can stay on the test
   rigs; ASP.NET hosting ships in Phase 9.
5. The fixture plugin project is in the solution under `tests/fixtures/`
   (not a platform project — it's a plugin implementation; architecture
   tests verify no platform project references it).