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
- Quality loop (skill `quality-loop`, `~/.pi/agent/skills/quality-loop/SKILL.md`):
  `python3 ~/.pi/agent/skills/quality-loop/scripts/dotnet/warnings-audit.py`,
  `.../metrics-audit.py`, `.../coverage-audit.py`, `.../dotnet/audit.py` (CRAP),
  `.../stryker-audit.py`; or the driver
  `python3 ~/.pi/agent/skills/quality-loop/scripts/quality-loop.py --dry-run`.
  Repo policy lives here: `.dependably`, `coverage-policy.json`.
  Queue/report artifacts are gitignored — never commit them.

## Quality gates (baselines 2026-09-11, remeasured 2026-09-12; loop supports .slnx + all 9 test projects)

- Warnings: green (zero build warnings, `--no-incremental`).
- Metrics (`.dependably`: MI ≥ 20, cyclomatic ≤ 25, …): 1 high —
  `SpatialClient` god-class (typed SDK facade, 25 thin per-route methods),
  plus moderate `PostgisStore` / low `ProjNetTransforms`, `StoreEndpoints`
  facades. Facade splits must stay cohesive per API area; see SKILL.md
  anti-gaming rules before refactoring or grandfathering. Dependably 0.1.2
  `exceptions` only suppress metric rules, not `god-class`/`hub`
  diagnoses (verified empirically) — the facades stay visible until a
  cohesive per-area split lands (own ADR).
- CRAP (`scripts/dotnet/audit.py`: solution-wide `dotnet test`, merged coverage):
  the merge canonicalizes coverlet's per-run-relative filenames (suffix
  unification, skill `coverage_merge.py`), without which every method
  counted twice (once covered, once phantom-uncovered). Remaining 0%
  flags are crap4dotnet's own method-matching limits: expression-bodied
  `switch` members, async state machines and accessors it cannot pair
  (see its UNMATCHED/ORPHANED warnings) — several are proven covered by
  the deduped data. Truly-complex-and-partly-uncovered methods are rare;
  the irreducible residue is one-line dead defensive arms (`_ => throw`
  on closed enums, verified unreachable) plus Docker-only PostGIS paths.
- Coverage (`coverage-policy.json` floor 70% branches): green — ~91%
  branches (~94% lines) on authored code after the merge fix (was 48.8%
  phantom). The queue lists genuinely untested authored methods
  worst-first; Docker-only `PostgisStore` live paths cover only in CI.
- Stryker: per-test-project runs (~11 min each × 9); run only when cheap
  gates are green.

## Definition of done

Implemented behaviour with typed contracts; success, failure and
cancellation tested; no prohibited dependency; actionable diagnostics;
docs/ADRs updated; clean `eng/verify.sh` from a clean checkout.
