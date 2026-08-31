# Handoff — Spatial Engine

> Written for the next agent taking over. Read `AGENTS.md`, then
> `architecture/implementation-plan.md` (the source of truth). **Phase 9
> (ASP.NET Core Host and SDKs) is complete**; Phase 10 (Browser Workbench) is
> next.

## Repository state

- **Branch:** `main`. Working tree clean at handoff. The four quality-gate
  queue/report files (`crap-queue.md`, `coverage-queue.md`,
  `metrics-queue.md`, `warnings-queue.md`, `*.report.json`,
  `coverage-history.csv`, `stryker-queue.md`) are gitignored and currently on
  disk reporting the ALL-GREEN state — do not commit them.
- **Phase 9 commits** (new since the Phase 8 handoff):
  - `6bfb111` — value codec graduates to `Spatial.PluginSdk.Codec.ValueCodec`
    (ex `WorkerValueCodec`, ADR-0030), HTTP contract shapes in
    `Spatial.PluginSdk.Http`, and the ASP.NET Core host runtime
    (`SpatialHostRuntime`, supervisor-backed package activation).
  - `bb57717` — host API integration tests (fixture provider, every endpoint,
    the real isolated NTS worker buffering through HTTP).
  - `83a089c` — .NET client SDK with stub-handler unit tests and full-stack
    integration tests against the real host.
  - `c99f216` — the TypeScript SDK (`clients/typescript`) generated from the
    host's OpenAPI, plus `eng/e2e-web.sh` and `eng/tools/PluginPacker`.
  - `3f983d5` — docs: ADR-0030, `architecture/host-api.md`, plan
    §12/§16/§21(§24 ticks), boundary doc updates, README status.
  - `2af82d1` — quality gates green: metrics fan-out splits, CRAP branch
    tests, coverage unit tests (incl. the loop implementor sessions' units),
    Stryker result recorded.
  - (final, after the quality gates) — this handoff.
- **Tests:** 936 total across 10 suites (Core 308, Runtime 201, PluginHost
  85, Host 42 [was 3], Client 9, PostGIS unit 170, NTS 31, ProjNet 65,
  Conformance 12, Architecture 8). `./eng/verify.sh` passes from a clean
  checkout (PostGIS integration still skips honestly without Docker).

## Completed — Phase 9 (Epic H: ASP.NET Core API + TypeScript SDK)

- **The host API** (`architecture/host-api.md`, ADR-0030): capabilities list/
  detail, `POST /api/invocations` (inline `completed` or `202 job` routing —
  long-running never parks the request), jobs (get/cancel/events: JSON page
  or SSE), resources (metadata/delete/stream → `application/x-ndjson` of
  codec-encoded items, `$error` line on failure), plugins (supervised worker
  packages + lifecycle), health, and OpenAPI at `/openapi/v1.json`.
- **One contract set**: the inline value codec moved from the worker protocol
  to `Spatial.PluginSdk.Codec.ValueCodec` (shared by worker wire + HTTP API);
  request/response shapes live in `Spatial.PluginSdk.Http`; the host configures
  its JSON options from `HostApiJson`; OpenAPI is generated from the same
  DTOs; the TS wire types are generated from the OpenAPI snapshot.
- **.NET SDK** (`clients/dotnet/Spatial.Client`, NOT in the solution — built/
  gated through `tests/unit/Spatial.Client.Tests` + `tests/integration/
  Spatial.Host.Tests`): typed methods over HttpClient, `ReadStreamAsync`,
  `ReadFeatureBatchesAsync` (SFBAT decode), `WaitForJobAsync` extension.
- **TypeScript SDK** (`clients/typescript/@spatial/client`, zero runtime deps):
  fetch-based client, wire codec (`$i64`/`$bytes`/`$geometry`/`$crs`/
  `$resource`, `encodeGeometry`), SFBAT v1 decoder pinned against a .NET
  produced vector, `scripts/generate.mjs` + `scripts/check-generated.mjs`
  (drift gate in `npm test`).
- **Independent-host proof**: `eng/e2e-web.sh` packs the NTS worker
  (`eng/tools/PluginPacker` → `artifacts/plugins`), runs the REAL host
  process, refreshes the OpenAPI snapshot, and drives it from the TS SDK
  over real HTTP — passes. `tests/integration/.../NtsWorkerHostTests.cs`
  proves the same in-suite (buffer through the isolated worker over HTTP).

## Hard-won gotchas (read before touching this code)

- **The Ca-7 ceiling is the hardest constraint (Spatial.Core.Geometry).**
  The codemetrics `architectural-rigidity` diagnosis fires at Ca ≥ 8 (D 0.91).
  Phase 9's metrics episode: `eng/tools/PluginPacker/Program.cs` used
  `typeof(Spatial.Core.Geometry.IGeometry)` to locate Spatial.Core.dll and
  pushed Ca to 8 — **avoid naming Core.Geometry types anywhere outside the
  core or the deliberate single referrers** (use a non-geometry Core type
  like `FeatureId` to locate the assembly). The host API layer reads feature
  data through `FeatureBatch`/`AttributeValue` (ADR-0029 faces) and never
  names an `IGeometry` — keep it that way.
- **Metrics coupling ceiling (in-repo coupling ≥ 20 → hub finding) and the
  god-class rule (coupling ≥ 15 + LCOM4 ≥ 3 + WMC ≥ 20 + ≥ 8 methods).** The
  quality audit gates on ZERO findings of any severity — moderate hubs count.
  The host API layer is deliberately split into small mapper classes
  (`CapabilityApiMappers`, `JobApiMappers`, `JobEventMappers`,
  `ResourceApiMappers`, `PluginApiMappers`, `InvocationOutcomeMapper`,
  `InvocationRequestBuilder`) and the client into `SpatialClient` +
  `SpatialStreamReader` + `SpatialClientExtensions`. **New endpoint/handler
  code must stay fan-out-small and delegate heavy loops to tiny helpers**
  (and every branch needs a test — CRAP < 10 gates on branch coverage; see
  the `TryBuildOptions` episode).
- **Jobs must NOT receive the request's CancellationToken.** `POST
  /api/invocations` builds the invocation with `CancellationToken.None` —
  job-runner links the job's own token + deadline; passing RequestAborted
  would cancel the job the moment the 202 response returns. Inline
  invocations DO use RequestAborted (client disconnect cancels them).
- **Consuming a stream to its end closes the resource** (the NDJSON reader
  closes in its finally). After reading a stream, its metadata is 404 —
  streams are one-shot. A failed stream ends with an `$error` line; the SDKs
  throw `CapabilityStreamException`/`CapabilityStreamError`.
- **The `$geometry` tag needs explicit encode on the TS side**: plain
  `Uint8Array` encodes as `$bytes`. The SDK's `encodeGeometry(bytes)` /
  `GeometryValue` emit `$geometry`. The hand-built SGEOM point in
  `test/e2e.test.ts` (26 bytes: magic/version/layout/type/crs/present + two
  zero doubles) matches `GeometryCodec` byte-for-byte — see
  `feature-batch.test.ts` for the .NET-produced pin vector.
- **Full GUIDs in SFBAT are .NET mixed-endian** (`Guid.TryWriteBytes`): the
  TS decoder flips the first three groups. The vector test pins it.
- **Node 26 runs TS tests directly** (type stripping): SDK and tests must be
  erasable-syntax — no parameter properties, no enums — `tsconfig` sets
  `erasableSyntaxOnly`. Import paths use `.ts` extensions +
  `allowImportingTsExtensions`.
- **`npm test` includes the generated-types drift check** — refresh with
  `npm run generate` (the e2e script does it from the live host). The
  generator header must NOT embed the output path (drift-check compares files
  written to different names).
- **`dotnet run` runs the app with CWD = the project directory**, so the
  e2e script passes an ABSOLUTE `Spatial__PackagesRoot` (env var double
  underscore maps to `Spatial:PackagesRoot`). launchSettings overrides
  `ASPNETCORE_URLS` — use `dotnet run --no-launch-profile --urls`.
- **Style traps from earlier phases still apply**: format before every
  commit (trailing newlines — `dotnet format`), CA1859/CA1826/CA1068 (last
  param CancellationToken), LoggerMessage delegates for ILogger
  (CA1848/CA1873 — see `HostLog.cs`), `Results<...>` typed results so
  OpenAPI gets schemas, `Produces<T>` annotations.
- **Codemetrics exit-code quirk**: `codemetrics` exits 1 even with zero
  findings; the audit gates on the reported finding count (0 = green). The
  stryker audit picks ONE pinned test project (`configured[0]` — currently
  `Spatial.Core.Tests`), so Stryker measures only Spatial.Core each full run.

## Quality gates (Phase 9 — all four green, verified twice)

- **CRAP**: 0 of 2411 methods ≥ 10.
- **Coverage**: authored branch 81.8% ≥ 70% (line 92.5%).
- **Metrics**: 0 findings (the Ca-8 and fan-out episodes above resolved).
- **Warnings**: 0.
- **Stryker (my call — RUN)**: the phase changed Runtime behavior
  (`ResourceRegistry.TryGetHandle`, `CapabilityJob.Resolved`) and moved the
  codec, so I ran the full gate: **94.44%** score on Spatial.Core (the
  project the audit pins; 974 killed, 17 surviving pre-existing mutants, 49
  no-coverage), break-60 **green**. The survivors (Core Feature/Field
  definitions, codec statements, envelope internals) predate Phase 9. The
  final repo-wide pass should add mutation configs for the NEW behavioral
  assemblies: `Spatial.PluginSdk` (codec + DTOs — needs a dedicated test
  project reference), `Spatial.Host`, and `Spatial.Client` (a
  `Spatial.Client.Tests` config would measure the client via its stub-handler
  suite).

## Next up — Phase 10: Browser Workbench

From the plan (§16 Epic H remainder, §17): React + TypeScript + MapLibre
workbench served by the Phase 9 host; browse PostGIS, render features,
select, invoke the buffer plugin, watch job progress, preview/persist results,
and demonstrate side-by-side plugin replacement — Playwright e2e, no Tauri.

1. **The API and SDKs are ready**: `clients/typescript/@spatial/client`
   (fetch-based, generated types, SFBAT decoder, stream reads, job
   wait/cancel/events) is the workbench's only channel to the host. `apps/
   workbench-web/` is the Phase 10 home (repo layout §15); the
   `Web_clients_do_not_depend_on_tauri` architecture test already guards it.
2. **Running a full stack for development**: `eng/e2e-web.sh` shows the
   pattern — pack `nts` (PluginPacker) or `postgis` (+
   `Spatial__PackagesRoot` + `SPATIAL_POSTGIS_CONNECTION` in
   `Spatial:WorkerEnvironment`), run the host with `--no-launch-profile`,
   point the client at it. For PostGIS browse the phase-8 conformance fixture
   (`bigpoints`, 200k rows) is the performance vehicle (plan §18).
3. **Stream decoding is in both SDKs**: the TS `readStream` +
   `decodeFeatureBatch` handle scans; `$geometry` attributes stay raw SGEOM
   bytes — the renderer needs a MapLibre geometry adapter (Phase 10's first
   real piece of client code). Job progress arrives via the SSE events
   endpoint or `waitForJob`.
4. **Plugin replacement demo (plan §17.9-11)**: the host has
   `CapabilityRuntime.SetActivePreference` (route new work to v2) and
   drain/rollback via the supervisor; the `/api/plugins` surface shows both
   versions. Phase 10 needs a second NTS package version to demonstrate
   side-by-side routing.
5. **Playwright**: not yet in the repo; the workbench tests live under
   `tests/end-to-end-web/` (plan §15) — out of the solution (like the
   clients), driven by an eng script + npm.
6. **Conformance/OpenAPI drift**: if Phase 10 changes the API, regenerate the
   TS types (`npm run generate`) and refresh
   `clients/typescript/scripts/openapi.snapshot.json` via `eng/e2e-web.sh`.