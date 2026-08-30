# Handoff — Spatial Engine

> Written for the next agent taking over. Read `AGENTS.md`, then
> `architecture/implementation-plan.md` (the source of truth). **Phase 6
> (NetTopologySuite operations plugin) is complete**; Phase 7 (coordinate
> transformation plugin) is next.

## Repository state

- **Branch:** `main`. Working tree clean at handoff... **except**: the four
  quality-gate queue/report files at the repo root (`crap-queue.md`,
  `coverage-queue.md`, `metrics-queue.md`, `warnings-queue.md`,
  `stryker-queue.md`, `*.report.json`, `coverage-history.csv`) are
  gitignored and currently exist on disk **reporting the FINAL GREEN state**
  (all four audits exit 0 on the current HEAD) — do not commit them.
- **Phase 6 commits:**
  - `232fdde` — geometry operation contracts in `Spatial.PluginSdk.Operations`
    (buffer/intersection/validate/simplify @1, ADR-0026) + the `$geometry`
    canonical-binary wire tag in the worker value codec (ADR-0020)
  - `a845be4` — `src/Spatial.Operations.NetTopologySuite` plugin
    (`nts@1`, NetTopologySuite 2.6.0) with the private core↔NTS adapter
  - `64e89f8` — plugin unit tests + the shared conformance suite
    (`tests/conformance`, runs same fixtures in-process and against the
    worker package)
  - `01e5735` — `architecture/operation-contracts.md`, plan ticks, README
  - `19adbf4` — operation-runner consolidation for the metrics gate (see
    gotchas below)
- **Tests:** 631 total (Core 308, Runtime 201, PluginHost 76 (43 s — spawns),
  Operations 31, Conformance 4 (7 s — spawns), Architecture 8, Host 3).
  `./eng/verify.sh` passes from this tree.

## Completed — Phase 6 (Epic G): NetTopologySuite operations plugin

- **Contracts (ADR-0026, `architecture/operation-contracts.md`)**: the four
  versioned capability contracts ship in `Spatial.PluginSdk.Operations` —
  `spatial.geometry.buffer@1` / `intersection@1` / `validate@1` /
  `simplify@1`. Each declares input/output schema names, one error variant
  (`invalid.arguments`), `Cancellable` traits (inline, never long-running)
  and shared conformance examples (`GeometryOperationConformanceExamples`)
  whose values are core geometry types only (ADR-0005). Argument names in
  `GeometryOperationArguments`.
- **Wire interchange**: geometry now crosses the worker boundary as canonical
  binary interchange in a `$geometry` wire tag (SGEOM bytes, base64) — the
  inline codec decodes to real `IGeometry` values and rejects malformed
  payloads with byte-accurate errors (tested in `ProtocolCodecTests`).
  Feature batches are still rejected (ADR-0020 hint). JSON cannot represent
  NaN/infinity, so the `non-finite-distance` example runs only in-process
  (the conformance suite skips wire-incompatible examples for the worker).
- **Plugin** (`src/Spatial.Operations.NetTopologySuite`, provider `nts@1`):
  NetTopologySuite 2.6.0. Private `Adapters/GeometryAdapter` converts every
  core shape ⇄ NTS (ring closure normalised — open rings get their first
  coordinate appended; Z/M ordinates carried where the planar algorithms
  preserve them; simplify keeps Z, buffer/intersection are XY; the input CRS
  identity goes on results; rings/children carry no CRS). Validation
  pre-checks OGC ring rules (≥4 coords, closed) that NTS LinearRings cannot
  represent, then `IsValidOp` — an invalid geometry is a successful `false`.
  NTS `TopologyException`/`ArgumentException` → `invalid.arguments` naming
  the cause; everything else → provider failure. Cancellation is honoured
  before the synchronous run.
- **Conformance** (`tests/conformance/Spatial.Conformance.Tests`): the
  shared suite (`GeometryOperationConformance`) runs the same success /
  empty-input / unsupported-input / cancellation / diagnostics / provenance
  fixtures against the provider **both in-process and as an isolated worker
  package** (supervisor + real child process, `$geometry` over the wire).
  Every outcome asserts provenance (capability, provider, step, duration,
  no job id). A unit suite pins the adapter round trips and the provider's
  argument contract; an ADR-0005 reflection test asserts no NTS type appears
  on the plugin's public surface.

## Quality gates (Phase 6 — all green)

All four audits exit 0 on HEAD: **CRAP 0** of 1571 methods (floor < 10);
**coverage 79.1% branch** (floor 70%); **metrics 0 findings**; **warnings 0**.

**Stryker (my call — NOT run).** The repo's only stryker-config.json pins
`Spatial.Runtime.csproj`, which this phase did not change; the phase's new
assemblies (PluginSdk contracts, PluginHost.DotNet codec, the NTS plugin)
have no mutation config. An ~11-min run would therefore measure unchanged
code and miss this phase entirely — skip is honest, not evasion. Last known
score (Phase 5 handoff): run-level 87.27% / last-scores 93.09%, break-60
green. **Recommendation for the loop's final repo-wide pass**: decide
whether to add a stryker config for `Spatial.Operations.NetTopologySuite`
(policy decision, like Phase 5's PluginHost one; the conformance project
spawns a worker, so mutations will be slow per-test).

## Hard-won gotchas (read before touching this code)

- **The metrics gate's `architectural-rigidity` diagnosis is a fan-in count
  on `Spatial.Core.Geometry`** — codemetrics fires it when ≥ 8 distinct
  *production types* reference the namespace (D ≥ 0.6, abstractness < 0.3,
  not data-only). Phase 5 had Ca 2; this phase's nine geometry-referencing
  types pushed it to 11 and tripped the HIGH diagnosis. It is an
  interpreted namespace diagnosis: **NOT suppressible via `.dependably`
  exceptions or failOn severity** (exceptions only apply to metric rules
  cyclomatic/cognitive/nesting/mi/lcom4/coupling; verified against the
  tool's source at /tmp/cm). The only levers are code: reduce the count of
  types referencing Core.Geometry (I consolidated the four duplicated
  operation runners + parsing + validity + failure mapping into one
  `NtsOperationRunner` — Ca now 6), or raise Core's abstractness (the
  quality loop's fixer added an `IPoint`/`ILineString` interface layer to
  Core — **I reverted it**: it rewrites the Phase 1 core's public surface
  for a gate artifact and violates the Core boundary; if a future phase
  tips Ca ≥ 8 again, prefer consolidation in the phase's own code).
- **The quality loop spawns autonomous fix sessions**: when I ran
  `quality-loop.py --skip stryker`, its headless implementor edited the
  tree itself (Core interfaces + a dispatch refactor) before I killed the
  loop after 30 min. Run the loop with a tight budget, or run the four
  audits directly (`scripts/dotnet/{audit,coverage-audit,metrics-audit,
  warnings-audit}.py`) and drive fixes yourself — the audits are
  deterministic and exit 0/1.
- **The loop's `.dependably` default excludes only `**/*.Tests/**`** — the
  fixture plugin project (`tests/fixtures/Spatial.Plugin.Fixtures/`) is
  scanned as a production namespace (Ca 0, no geometry refs — fine today,
  but keep it that way; NTS mutations there would count).
- **NTS 2.6 changed the coordinate classes**: plain `Coordinate` is 2D
  (`Coordinate.Z` setter throws); 3D is `CoordinateZ`. The adapter never
  touches coordinate classes — it builds sequences with
  `CoordinateArraySequenceFactory.Create(size, dimension, measures)` and
  reads ordinates via `GetOrdinate(i, Ordinate.X/Z/M)` + `HasZ`/`HasM`.
  Buffer/intersection output is dim-2 (XY); simplify preserves Z.
- **A namespace named `Spatial.Operations.NetTopologySuite...` shadows the
  `NetTopologySuite` root namespace** — any qualified `NetTopologySuite.…`
  reference inside the plugin's namespaces fails to resolve. Use aliases
  (`using NtsGeometry = …`, `NtsTopologyException = …`).
- **`CapabilityId` is a record struct — it keys a `Dictionary` fine**; the
  provider dispatch is now a table (`Handlers`) which also killed the
  `deep-nesting` metrics finding the ternary chain caused.
- **`Progress<T>` / spawn-test gotchas from Phase 5 still apply**; ditto
  `Assert.NotNull` binds to the void overload (use `Assert.IsType`), cc ≤ 9
  per method, final newline in every file.
- **Conformance on the worker path**: pre-cancelled caller tokens yield
  `Cancelled` through the supervisor (`WorkerChannel.RequestAsync` aborts on
  the linked token) even though the wire invoke has no token — the shared
  cancellation fixture passes on both paths.
- **`TryOptionalPositiveInt` accepts int32 and int64** wire forms (JSON
  numbers decode to int, `$i64` to long) — the conformance example passes
  `12L`; a strict `TryGetArgument<int>` would reject the wire form.

## Next up — Phase 7: Coordinate Transformation Plugin

From the plan (§16, Epic G): define CRS description and transformation
contracts; implement the selected transformation library adapter; add
control-point, axis-order, error and tolerance tests. What Phase 6 leaves
ready:

1. **The patterns to copy wholesale**: contracts in
   `Spatial.PluginSdk.Operations`-style static classes (+ ADR), a plugin
   project with a private adapter (NTS-free though — Phase 7 picks its own
   library), unit tests pinning the adapter, and the conformance suite that
   runs the same fixtures in-process and against the worker package
   (`NtsManifest` deriving the manifest from the provider's descriptors +
   `NtsPackageWriter` copying the plugin's dependency assemblies — a Proj
   library would be copied the same way).
2. **Plan §16 Phase 7 tick list**: "Define CRS description and
   transformation contracts" (Phase 7 owns ADR-0009's *transformation is a
   plugin* contract shape), "Implement the selected transformation library
   adapter", "Add control-point, axis-order, error and tolerance tests".
   Epic G's remaining unticked bullets are "Transformation provider" and
   "PostGIS provider".
3. **The `$geometry` wire tag is the interchange for CRS-transformed
   results too** — no wire change needed; only new contract ids
   (e.g. `spatial.transform.*@1` or `spatial.crs.describe@1`).
4. **Mind the metrics fan-in gotcha**: a transformation plugin will add
   more Core.Geometry referrers — keep the probe at Ca < 8 for
   `Spatial.Core.Geometry` (consolidate; today Ca 6).
5. **Host wiring is still Phase 9**; Phase 7 can stay on the test rigs like
   Phase 6 did.