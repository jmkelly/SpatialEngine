# Handoff — Spatial Engine

> Written for the next agent taking over. Read `AGENTS.md`, then
> `architecture/implementation-plan.md` (the source of truth). **Phase 7
> (coordinate transformation plugin) is complete**; Phase 8 (PostGIS
> provider) is next.

## Repository state

- **Branch:** `main`. Working tree clean at handoff. The four quality-gate
  queue/report files at the repo root (`crap-queue.md`, `coverage-queue.md`,
  `metrics-queue.md`, `warnings-queue.md`, `*.report.json`,
  `coverage-history.csv`) are gitignored and currently on disk reporting the
  GREEN state — do not commit them.
- **Phase 7 commits:**
  - `0229241` — transformation contracts in `Spatial.PluginSdk.Transformations`
    (`spatial.crs.describe@1`, `spatial.coordinate.transform@1`, `CrsIdentity`,
    `CrsDescription`, ADR-0027) + the `$crs` wire tag in `WorkerValueCodec`
  - `b77196c` — `src/Spatial.Transformations.ProjNet` plugin (`projnet@1`,
    ProjNet 2.1.0) with the curated programmatic EPSG catalogue + 39 unit tests
  - `040b00c` — the shared transformation conformance suite (in-process +
    isolated worker package)
  - `f212660` — ADR-0027, `architecture/transformation-contracts.md`, plan
    ticks, README
  - `245e192` — quality-gate refinements (codec/runner complexity splits,
    fan-in ceiling held at Ca 7)
- **Tests:** 685 total (Core 308, Runtime 201, PluginHost 78 (44 s — spawns),
  ProjNet 48, NTS 31, Conformance 8 (7 s — spawns), Architecture 8, Host 3).
  `./eng/verify.sh` passes from this tree.

## Completed — Phase 7 (Epic G): coordinate transformation plugin

- **Contracts (ADR-0027, `architecture/transformation-contracts.md`)**: two
  versioned capability contracts in `Spatial.PluginSdk.Transformations` —
  `spatial.crs.describe@1` (input `crs.identity`, output `crs.description`
  carried as a new `$crs` wire value) and `spatial.coordinate.transform@1`
  (input `geometry.transform` — a geometry, optional `source` defaulting to
  the geometry's own CRS, required `target`; output geometry stamped with
  the target CRS). One error variant `invalid.arguments` naming the value.
  **Axis convention**: the engine is x-first for every CRS (x = longitude /
  easting); describe reports declared axes. The describe contract's
  conformance examples (string-only) are embedded in the contract; the
  transform fixtures ship **with the conformance suite** (`Transform
  ConformanceExamples`) — a fan-in-budget decision documented in ADR-0027.
- **Wire**: `CrsDescription` crosses the worker boundary as a `$crs` inline
  tag (same explicit-tag rule as `$geometry`, ADR-0020/0025); malformed
  payloads fail with field-level errors. The `$geometry` tag carries
  transformed results unchanged — no other codec change.
- **Plugin** (`src/Spatial.Transformations.ProjNet`, provider `projnet@1`):
  ProjNet 2.1.0. The embedded **curated EPSG catalogue** (`ProjEpsgCatalog`,
  12 CRSs) is built programmatically through ProjNet's factory — NOT WKT
  parsed, because ProjNet 2.1's WKT reader maps "Popular Visualisation
  Pseudo-Mercator" to Mercator_1SP (~33 km northing error). Datum shifts are
  zero for modern datums, the classic Helmert for OSGB36 (no grid support).
  Control points verified against PROJ 9 (pyproj): sub-millimetre for
  datum-free pairs, 0.1 m tolerance (documented) for the Helmert path.
- **Adapter** (`ProjNetTransformRunner` + `ProjNetCrsMapper`): engine
  convention == ProjNet 2.1 math order → no axis swaps; Z/M and layout kept;
  non-finite out-of-area results are `invalid.arguments`, never poisoned
  geometry; mismatched explicit source vs geometry CRS is invalid; empty
  geometries keep type/layout with target CRS.

## Quality gates (Phase 7 — all green)

All four audits exit 0 on HEAD: **CRAP 0 of 1714 methods** (threshold < 10);
**coverage 79.4% branch** (floor 70%); **metrics 0 findings**
(`Spatial.Core.Geometry` Ca 7 — the fan-in ceiling, see gotchas); **warnings 0**.

**Stryker (my call — NOT run).** The repo's stryker-config.json pins
`Spatial.Runtime.csproj`, which this phase did not change; the phase's new
assemblies (PluginSdk.Transformations contracts, ProjNet plugin, the codec's
`$crs` extension) have no mutation config. Same reasoning as Phase 6: an
~11-min run would measure unchanged Runtime code and miss this phase. Last
known score (Phase 5 handoff): run-level 87.27% / last-scores 93.09%,
break-60 green. **Recommendation for the loop's final repo-wide pass**: add
stryker configs for `Spatial.Transformations.ProjNet` and (now that the
codec carries `$geometry`/`$crs`/`$resource`) `Spatial.PluginHost.DotNet` —
both were policy decisions like Phase 5's PluginHost one.

## Hard-won gotchas (read before touching this code)

- **The Ca-7 ceiling is the hardest constraint in this repo.** The metrics
  gate fires the HIGH `architectural-rigidity` diagnosis when ≥ 8 production
  types reference `Spatial.Core.Geometry` (D 0.91, abstractness 0.09 —
  not configurable via `.dependably`, verified against the tool at
  /tmp/cm/src). Phase 7 sits at Ca 7, exactly **one slot from red**. Phase 8's
  PostGIS provider MUST plan for this: reading PostGIS rows → `IGeometry`
  values will need geometry-referencing production types. Consolidate into
  one type, reuse `WorkerValueCodec`-style geometry marshalling, or — the
  strongest option — put the feature/geometry conversion in ONE interior
  class. Watch **nested records**: a nested `record TransformArguments` that
  held an `IGeometry` field tripped Ca 7→8 mid-phase (I moved the geometry
  out of the record). Nested types count as separate referrers.
- **The CRAP tool (crap4dotnet 0.1.1) uses the CUBIC formula
  `cc²×(1−cov)³+cc` and rates conditional methods by BRANCH-rate** (from the
  cobertura `<method branch-rate>` when conditions exist). Consequences: a
  method with cc ≥ 10 fails even at 100% coverage (split it); switch/chain
  methods must have **reachable branches only** — an unreachable `default`
  arm caps branch coverage and fails cc ≥ 7ish. Phase 7's fix pattern:
  dispatch **tables** for per-type/per-enum mapping (the composite geometry
  table, the orientation lookup) instead of exhaustive switches with dead
  default arms; extracted `TryX` helpers for argument chains.
- **ProjNet 2.1 specifics**: (1) inside any namespace under
  `Spatial.Transformations.ProjNet`, bare `ProjNet.…` references shadow to
  the plugin namespace — use file-level aliases (`using ProjCs =
  ProjNet.CoordinateSystems;` resolves globally) or `global::`. (2) The WKT
  reader misclassifies Pseudo-Mercator (catalogue is programmatic). (3)
  Projected CRSs expose their datum via `GeographicCoordinateSystem.
  HorizontalDatum`; geocentric via its own `HorizontalDatum`; the inherited
  one is null. (4) No custom exception types — map
  `ArgumentException`/`NotSupportedException`/`FormatException`. (5) Static
  field init order matters: `Entries = BuildEntries()` must be declared
  after the `Geographic`/`Projected` arrays it reads (NRE otherwise).
- **coverage collection noise**: the merged cobertura mixes filename styles
  per test project (`src/…` for PluginHost tests, `ProjectName/…` for
  cross-project runs) and crap4dotnet matches by canonical key; expect
  hundreds of `[UNMATCHED_METHODS]` warnings. It's noise as long as the
  FLAGGED list stays empty — verify by watching the gate, not the warnings.
  Keep the queue files out of commits (gitignored).
- **The worker-package pattern to copy** (already in `tests/conformance`):
  `NtsManifest`/`NtsPackageWriter` → for Phase 7, `ProjNetManifest`/
  `ProjNetPackageWriter` copies `ProjNET.dll` next to the plugin assembly
  (the conformance project is the only project that references the plugin,
  so coverlet covers it from both runs — fine). `WorkerValueCodec` decodes
  `$crs` only for `JsonObject` tags — the reject-path message lists all
  supported tags; keep it in sync when adding tags in Phase 8 (feature
  batches still cross only as streams — ADR-0020).
- **Style traps**: `dotnet format` before every commit (the write tool drops
  trailing newlines; the format check fails otherwise — FINALNEWLINE/
  WHITESPACE/IMPORTS). CA1859 wants private methods/fields concrete-typed
  (`Dictionary`, `PackedCoordinateSequence`…). xUnit2018 forbids
  `Assert.IsType<IGeometry>` (cast instead); xUnit1031 forbids
  `.GetAwaiter().GetResult()` in test methods (await). The formula-gate
  means **about half the branch coverage burden is "keep methods small"** —
  per AGENTS.md.
- **The `$crs` decode tests live in `ProtocolCodecTests`**; the conformance
  `$crs` round trip goes through the real worker in
  `WorkerTransformConformanceTests`.

## Next up — Phase 8: PostGIS Provider

From the plan (§16, Epic G): catalogue, schema discovery, feature scan,
filtering, streaming, writing and transactions; host-managed secrets and
command cancellation; containerised integration tests. What Phase 7 leaves
ready:

1. **The patterns to copy wholesale**: contracts in
   `Spatial.PluginSdk…` static classes (+ ADR), a plugin project with private
   adapters (PostGIS-backed), unit tests, and the shared conformance rig that
   runs the same fixtures in-process and against the worker package. The
   transform provider shows the worker-package flow end to end including the
   dependency copy.
2. **The fan-in budget is spent**: `Spatial.Core.Geometry` is at Ca 7 —
   Phase 8's row→geometry conversion must fit in the remaining **one**
   referrer slot (or consolidate an existing one, e.g. fold `WorkerValueCodec`
   dependencies — risky; prefer a single interior conversion type).
3. **Plan §16 Phase 8 ticks** are the Epic G bullets "PostGIS provider" plus
   the phase bullets (catalogue, schema discovery, scan/filter/stream/write/
   transactions, secrets, cancellation, containerised integration tests).
   `architecture/security-model.md` covers provider secrets; the plan §18
   test matrix covers conformance + integration (the audit already mentions
   Testcontainers Postgres ~40–60 s).
4. **Wire**: feature batches still cross the worker boundary only as streams
   (ADR-0020); the inline codec rejects them deliberately — Phase 8's
   streaming path is the Phase 4 `Streaming` trait + `$resource` handles, not
   a new inline tag.
5. **PostGIS needs a `postgis@1`-style provider id and its own curated
   capabilities** (e.g. `spatial.feature.scan@1`, `spatial.feature.query@1`,
   `spatial.feature.write@1` from plan §9); the geometry interchange for
   results is the existing `$geometry` tag.