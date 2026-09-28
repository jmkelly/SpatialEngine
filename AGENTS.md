# Spatial Engine

Spatial **values** live in `Spatial.Core`; the **verbs** are `Spatial.Contracts`
interfaces implemented in-process and composed by `Spatial.Host` with DI. The
browser workbench is the delivered client; Esri GeoServices REST is the
interop surface.

## Hard walls

- Spatial algorithms live in implementation projects; `Spatial.Core` stays
  structural: inspection, traversal, encoding, envelopes.
- Public contracts carry only core types; NTS, Npgsql, EF and renderer types
  stay inside their owning implementation.
- `Spatial.Contracts` references only `Spatial.Core` and takes no packages.
- The host is JIT-compiled; a Native AOT change needs an approved ADR first.
- Long-running work is a cancellable `Task`; failures are structured
  `SpatialException` codes (`invalid.arguments`, `not.found`,
  `store.unavailable`).
- Behaviour changes land contract, SDK, test and ADR updates together, with
  success, failure and cancellation tested. Test-first: every defect or gap
  gets a failing reproduction test before the fix, and the fix is confirmed by
  that test passing. Pin package versions in `Directory.Packages.props`.

## Commands

- `eng/verify.sh` — format, build, full tests; the gate before done.
- `bd` — the development task queue (capture, claim, status). Run `bd prime`
  for the full agent workflow.
- `eng/e2e-web.sh`, `eng/workbench-e2e.sh` — real host + delivered clients.
- `eng/seed.sh` — on-demand realistic dataset: fetch public data, ingest
  (with engine-side reprojection) and publish styled feature/map services.
- Quality loop: read the `quality-loop` skill first; repo policy is
  `.dependably` and `coverage-policy.json`.

## Task queue

Work is tracked in [beads](https://github.com/gastownhall/beads) (`bd`), not in
git. `bd ready` is where to start; `bd prime` prints the full agent workflow.

- Capture: `bd create --title="…" --description="…" --priority 2 -l <area>`
- Pick: `bd ready` then `bd update <id> --claim` — never start unclaimed work.
- Hand off: label the bead `needs-merge` with the commit and PR in `--notes`.
- Complete: `bd close <id> --reason="…"` — only after `eng/verify.sh` is green
  on `main`, never on the branch.
- Recovery: `bd reclaim` after a crashed agent's lease expires.

Records: a bead that will write a decision record **reserves** its number with
`python3 tools/adr-next-number.py --reserve --bead <id>` at the start of the
branch, and re-runs `--check NNNN` immediately before writing — the number is
read from `origin/main` and the reservation is held in the repository's shared
git dir, so a parallel branch that takes the same number is turned away at
allocation instead of at merge (ADR-0089). `--list` shows who holds what,
`--release NNNN` gives a number back after a renumber.

Areas are labels and route through `architecture/distilled/README.md`. The
database lives in the **git common dir** (`.beads/` beside the shared `.git`),
so every worktree sees one queue — `bd where` prints the resolved path. It is
local coordination state, not history: beads versions it in Dolt, and commit
provenance goes in the bead plus the `Task: <id>` commit trailer. Bead ids
issued before the migration are the old `T-NNN` sequence; new ones are
`T-<hash>`. This is development infrastructure, so it carries no ADR.

Migration note: the previous `eng/tasks` SQLite queue was replaced by beads on
2026-09-27. `tools/migrate-tasks-to-beads.py` is the one-shot mapping (status,
priority 1..5 → 0..4, area → label, agent/branch/commit → notes) and is kept
only as the record of that mapping.

## Detail on demand

- Architecture, contracts, host, Esri, ingest, rendering — route by task
  through `architecture/distilled/README.md`; the ADRs in
  `architecture/decisions/` win on conflict.
