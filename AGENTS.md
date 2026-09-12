# Spatial Engine

Headless, extensible spatial engine: a small .NET 10 core owns spatial
**values**; service interfaces in `Spatial.PluginSdk` own the **verbs**
(algorithms, stores, transformations), implemented in-process by the
`Spatial.Operations.*`, `Spatial.Transformations.*` and
`Spatial.Provider.*` projects and composed by `Spatial.Host` with
Microsoft DI (ADR-0033). The browser workbench is the delivered client and
Esri GeoServices REST (serve/consume/edit) is the interop surface. Source of
truth: `architecture/decisions/` (ADRs) and
`architecture/geoservices-implementation-plan.md`.

## Boundaries (hard walls — enforced by tests/architecture)

- Geometry values live in `Spatial.Core`; spatial algorithms ship in
  implementation projects. The core stays structural: inspection,
  traversal, encoding, envelopes.
- Public contracts carry only core types. NTS, Npgsql, EF and renderer
  types stay inside their owning implementation, never crossing a boundary.
- `Spatial.PluginSdk` holds interfaces over core types only; it takes no
  packages and references nothing but `Spatial.Core`.
- `Spatial.Host` links Core, SDK and implementations and runs standalone
  with no desktop shell; the browser workbench talks to it directly.
- The host is JIT-compiled. Native AOT requires an approved ADR plus measured
  benefit and compatibility evidence.
- Long-running behaviour is a cancellable `Task` with structured
  `SpatialException` diagnostics (`invalid.arguments`, `not.found`,
  `store.unavailable`).
- Public changes update contracts, SDKs, tests and ADRs together; versions
  live in `Directory.Packages.props`, never inline.

## Where to look

- Start here → `architecture/distilled/` (condensed docs; `README.md` routes by task)
- Core/geometry changes → `architecture/distilled/core.md`, `src/Spatial.Core/AGENTS.md`
- Services/operations/stores → `architecture/distilled/runtime.md`, `architecture/distilled/contracts.md`
- Host API, workbench, deployment, secrets → `architecture/distilled/host-and-clients.md`
- Esri GeoServices REST (serve/consume/edit) → `architecture/geoservices-implementation-plan.md`, ADR-0035, ADR-0037

## Commands

- `eng/verify.sh` — format check + build + full test run; required before done
- `eng/build.sh`, `eng/test.sh`, `eng/format.sh [--check]`
- `eng/e2e-web.sh` (real host + TypeScript SDK) and
  `eng/workbench-e2e.sh` (real host + built workbench + Playwright) — the
  delivered client paths; both run in CI
- CI `.github/workflows/ci.yml` runs verify + the JavaScript suites + both
  e2e scripts on every push/PR; `Dockerfile` packages host + workbench
  (`RELEASING.md` has the version/tag checklist)
- Quality loop (skill `quality-loop`, `~/.pi/agent/skills/quality-loop/SKILL.md`):
  `python3 ~/.pi/agent/skills/quality-loop/scripts/dotnet/warnings-audit.py`,
  `.../metrics-audit.py`, `.../coverage-audit.py`, `.../dotnet/audit.py` (CRAP),
  `.../stryker-audit.py`; or the driver
  `python3 ~/.pi/agent/skills/quality-loop/scripts/quality-loop.py --dry-run`.
  Repo policy lives here: `.dependably`, `coverage-policy.json`.
  Queue/report artifacts are gitignored — never commit them.

## Quality gates (remeasured 2026-09-12 after the GeoServices proof; loop supports .slnx + all 9 test projects)

Last full measurement: warnings 0, coverage 84.8% branches authored
(95.5% lines), **CRAP red (15/1058 methods ≥ 10)**, **metrics red
(3 high + 1 moderate after the ADR-0040 recalibration)**. The earlier "1 high" metrics baseline was
stale (pre-refactor report); the queue is the source of truth.

- Warnings: green (zero build warnings, `--no-incremental`).
- Metrics (`.dependably`, ADR-0040: MI ≥ 20, cyclomatic ≤ 15,
  cognitive ≤ 15, nesting ≤ 4, coupling ≤ 40, LCOM4 via the tool's
  guard-aware diagnoses, `failOn: moderate`): **red — 3 high, 1
  moderate, 10 low**. The recalibrated gate fails on `FeatureService`
  in-repo coupling 51, `FeatureService.EditsAsync` cognitive 17,
  `SpatialClient` god-class (high) and `PostgisStore` god-class
  (moderate); the 4 hubs and 6 long-parameter-lists are reported but do
  not gate. The raw `lcom4` rule is off because LCOM4 is meaningless for
  stateless types; a stateful class is flagged through `low-cohesion` /
  `god-class` instead (ADR-0040). Facade splits must stay cohesive per
  API area; see SKILL.md anti-gaming rules before refactoring or
  grandfathering. Dependably 0.1.2 `exceptions` only suppress metric
  rules, not `god-class`/`hub` diagnoses — the facades stay visible until
  a cohesive per-area split lands.
- CRAP (`scripts/dotnet/audit.py`: solution-wide `dotnet test`, merged coverage):
  the merge canonicalizes coverlet's per-run-relative filenames (suffix
  unification, skill `coverage_merge.py`). **Red — 15 of 1058 methods
  ≥ 10.** Three are complexity-bound and cannot pass on coverage alone
  (minimum CRAP = cyclomatic): `EsriFilterClause.EvaluateConstant`
  (cx 14), `FeatureService.EditsAsync` (cx 12),
  `FeatureService.UpdateRangeAsync` (cx 10); the rest (cx 4–9, low
  coverage) could pass with tests: `CompareSameKind`/`CompareByKind`,
  `EsriErrorMapper.Map`, `EsriFeatureCodec.ResolveGeometryField`,
  `Envelope.ThrowIfInvalid`, `EsriGeometryCodec.ReadCoordinate`.
  crap4dotnet's own UNMATCHED (15) / ORPHANED (408, 29%) warnings flag
  expression-bodied `switch` members, async state machines and
  accessors it cannot pair — not all 0% flags are truly uncovered.
- Coverage (`coverage-policy.json` floor 70% branches): green — 84.8%
  branches (95.5% lines) on authored code (was 48.8% phantom before the
  merge fix; the stale 09-11 report claimed 91.2%). The queue lists
  genuinely untested authored methods worst-first; Docker-only
  `PostgisStore` live paths cover only in CI.
- Stryker: per-test-project runs (~11 min each × 9); run only when cheap
  gates are green.

## Definition of done

Implemented behaviour with typed contracts; success, failure and
cancellation tested; no prohibited dependency; actionable diagnostics;
docs/ADRs updated; clean `eng/verify.sh` from a clean checkout.
