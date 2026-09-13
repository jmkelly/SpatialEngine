# Spatial Engine

Spatial **values** live in `Spatial.Core`; the **verbs** are `Spatial.PluginSdk`
interfaces implemented in-process and composed by `Spatial.Host` with DI. The
browser workbench is the delivered client; Esri GeoServices REST is the
interop surface.

## Hard walls

- Spatial algorithms live in implementation projects; `Spatial.Core` stays
  structural: inspection, traversal, encoding, envelopes.
- Public contracts carry only core types; NTS, Npgsql, EF and renderer types
  stay inside their owning implementation.
- `Spatial.PluginSdk` references only `Spatial.Core` and takes no packages.
- The host is JIT-compiled; a Native AOT change needs an approved ADR first.
- Long-running work is a cancellable `Task`; failures are structured
  `SpatialException` codes (`invalid.arguments`, `not.found`,
  `store.unavailable`).
- Behaviour changes land contract, SDK, test and ADR updates together, with
  success, failure and cancellation tested; pin package versions in
  `Directory.Packages.props`.

## Commands

- `eng/verify.sh` — format, build, full tests; the gate before done.
- `eng/tasks` — the local development task queue (capture, claim, status).
- `eng/e2e-web.sh`, `eng/workbench-e2e.sh` — real host + delivered clients.
- `eng/seed.sh` — on-demand realistic dataset: fetch public data, ingest
  (with engine-side reprojection) and publish styled feature/map services.
- Quality loop: read the `quality-loop` skill first; repo policy is
  `.dependably` and `coverage-policy.json`.

## Task queue

Work is tracked in a local SQLite queue, not in git. Drive it with
`eng/tasks` (details in `tools/tasks/README.md`):

- Capture: `eng/tasks add "…" --area <area> --priority 3`
- Pick: `eng/tasks next --claim --agent <id> --json` — never start unclaimed work.
- Hand off: `eng/tasks submit <id> --commit <sha> --pr <n>` (status `review`).
- Complete: `eng/tasks done <id>` — only after `eng/verify.sh` is green on
  `main`; `--verify` runs the gate first.
- Recovery: `eng/tasks reclaim` after a crashed agent's lease expires.

Areas route through `architecture/distilled/README.md`. The DB is shared by
all git worktrees (`eng/tasks where`) and is local state, not history: record
provenance with the `Task: <id>` commit trailer. This is development
infrastructure, so it carries no ADR.

## Detail on demand

- Architecture, contracts, host, Esri, ingest, rendering — route by task
  through `architecture/distilled/README.md`; the ADRs in
  `architecture/decisions/` win on conflict.
