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

- `eng/verify.sh` — the **build gate**, and the default: build the solution,
  then the test projects that reach what this branch changed over
  `ProjectReference`, plus `Spatial.Architecture.Tests` (and the python tooling
  tests when `tools/**` changed). Minutes rather than a quarter of an hour, and
  what every agent runs while iterating.
- `eng/verify.sh --format` — `dotnet format --verify-no-changes` scoped to the
  projects owning the changed files (~45 s each, against ~700 s for the whole
  solution). A pre-handoff step: run it before handing a bead off.
- `eng/verify.sh --full` — the **full gate**: format over the whole solution,
  build, every test project, the python tooling tests. This is the **merge
  gate**, and it is not an agent's step: the coordinator runs it on the
  rebased branch before a merge, and CI runs it on every pull request and again
  on `main` after the merge, so a formatting violation is caught by the merge
  rather than by an agent's inner loop. `main` has no branch protection and its
  CI is red today, so the coordinator's run is what currently enforces it
  (ADR-0118 §4; SpatialEngine-ivp, SpatialEngine-bv4). Run it locally when a
  change needs the whole thing before it goes near a PR.
  `eng/verify.sh --plan` prints what a lane would run and runs nothing.
  `CI=true` with no lane named selects `--full`, so a workflow that calls the
  bare script gets the gate rather than the build gate (ADR-0118).
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
- Hand off: label the bead `needs-merge` with the commit and PR in `--notes`,
  after `eng/verify.sh` and `eng/verify.sh --format` are green on the branch.
  That is the agent's whole step — `--full` is the merge gate, not an
  agent's step.
- Merge and complete: `python3 tools/bd-merge-bead.py --bead <id>` — never a
  hand-rolled `git merge` + `bd close`. It fetches, rebases onto `origin/main`,
  runs the full lane (`eng/verify.sh --full`) on the rebased branch, merges,
  **pushes**, and only then closes, and only after
  `git merge-base --is-ancestor <merge> origin/main` passes. A green
  `eng/verify.sh` on the *branch* is not a close gate: a branch is not what the
  next worker starts from, a merge left unpushed makes `origin/main` — the
  base every worktree is branched off — miss work that is already merged, and
  under ADR-0118 the bare `eng/verify.sh` is the build gate, so the merge gate
  names `--full` explicitly (SpatialEngine-xbz, SpatialEngine-u2x.51).
  `--check` is the top-of-tick gate (is `main` published?),
  `--publish` pushes it, and `--audit` finds closed beads whose work never
  reached `origin/main`.
- Complete: `bd close <id> --reason="…"` — only after the full lane
  (`eng/verify.sh --full`) is green on `main`, never on the branch. The default
  lane is never the only thing that ran.
- Recovery: `python3 tools/bd-safe-reclaim.py` after a crashed agent's lease
  expires — never bare `bd reclaim`. `bd reclaim` keys on lease age alone and a
  long-running worker does not heartbeat, so an expired lease only means "this
  agent has not run `bd heartbeat` lately": one coordinator tick released eight
  leases whose agents were all still running, dropping live work back into
  `bd ready` for a double-claim. The wrapper cross-checks the stale set against
  `paseo ls` — the bead's `bd/<id>` worktree, or the `agent <id>` its notes
  recorded at claim time — and reclaims only the beads no live agent holds,
  reporting each one it leaves in place. `--dry-run` reports the verdicts and
  reaps nothing; a worker can renew its own lease mid-bead with
  `python3 tools/bd-safe-reclaim.py --heartbeat <id>` (a plain `bd heartbeat
  <id>` does the same). Reclaim stays reversible: record the agent id and
  branch in the notes when you claim, and treat "reclaimed N" as a prompt to
  check `paseo ls` before those beads re-enter `bd ready`.

Records: a bead that will write a decision record **reserves** its number with
`python3 tools/adr-next-number.py --reserve --bead <id>` at the start of the
branch, and re-runs `--check NNNN` immediately before writing — the number is
read from `origin/main` and the reservation is held in the repository's shared
git dir, so a parallel branch that takes the same number is turned away at
allocation instead of at merge (ADR-0090). `--list` shows who holds what,
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
