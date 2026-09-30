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

- `eng/verify.sh` — the **fast gate**, and the default: builds the projects
  this branch's change reaches (a generated scoped solution, not all fifty) and
  runs the test projects that reach it over `ProjectReference`, plus
  `Spatial.Architecture.Tests` (and the python tooling tests when `tools/**`
  changed). Minutes rather than a quarter of an hour, and it is the lane the
  merge tool runs (ADR-0134).
- `eng/verify.sh --full` — the **exhaustive gate**: format over the whole
  solution, build, every test project, the python tooling tests. Opt in per
  merge (`bd-merge-bead.py --bead <id> --full`) when a change is broad enough to
  doubt the scoping; CI runs it on every pull request and again on `main` after
  the merge. It is not an agent's step and not a hand-off step
  (ADR-0118 §4; SpatialEngine-ivp, SpatialEngine-bv4; ADR-0134).
- `eng/verify.sh --format` — the format lane, `dotnet format
  --verify-no-changes` scoped to the projects owning the changed files (~45 s
  each, against ~700 s for the whole solution). Deliberately off the merge
  path: formatting is enforced by CI and by an occasional run rather than paid
  for hundreds of times a day (ADR-0134), so it is not a hand-off step.
- `eng/verify.sh --fast` — the same as no arguments, named so `CI=true`
  cannot promote a merge to the exhaustive lane. `--skip-tests <substring>`
  (repeatable, or `VERIFY_SKIP_TESTS`) leaves a named suite to CI on one run;
  it prints what it dropped and cannot drop `Spatial.Architecture.Tests`.
- `eng/verify.sh --plan` prints what a lane would run and runs nothing.
  Every lane starts with `tools/trailing_whitespace.py`, because `dotnet
  format` does not enforce the `trim_trailing_whitespace` the `.editorconfig`
  claims for `[*]` on a comment-only line (ADR-0143), and with
  `tools/conflict_markers.py`, which reads every tracked file for an
  unresolved merge-conflict marker — a rule that was a `tools/test_*.py` and so
  ran only on a change set that touched `tools/**`, which is how a marker
  reached `CHANGELOG.md` on main through a docs merge (ADR-0146).
  `CI=true` with no lane named selects `--full`, so a workflow that calls the
  bare script gets the exhaustive gate rather than the fast one (ADR-0118).
  Every lane that runs `dotnet test` also fails a suite that skipped most of
  what it was asked to run, read back out of the trx files
  (`tools/skip_gate.py`, ADR-0139): `VERIFY_SKIP_RATIO` (0.5) and
  `VERIFY_MIN_SKIPPED` (10) are the thresholds, and a suite dropped by
  `--skip-tests` is out of the count rather than judged.
- `bd` — the development task queue (capture, claim, status). Run `bd prime`
  for the full agent workflow.
- `eng/e2e-web.sh`, `eng/workbench-e2e.sh` — real host + delivered clients.
- `eng/seed.sh` — on-demand realistic dataset: fetch public data, ingest
  (with engine-side reprojection) and publish styled feature/map services.
- Quality loop: read the `quality-loop` skill first; repo policy is
  `.dependably` and `coverage-policy.json`. `eng/quality-audit.sh` aggregates
  the code audits with the documentation audit (`tools/doc-freshness.py`,
  SpatialEngine-imz.2) into one answer, and every lane runs it with
  `--report`: it fills `doc-queue.md` / `doc-report.json` and cannot fail a
  lane. The three cheap exact doc checks (register row, record schema,
  dangling `ADR-NNNN` citation) are read from `tools/arch-index.py` and *are*
  gates, through the doc gate above.

## Task queue

Work is tracked in [beads](https://github.com/gastownhall/beads) (`bd`), not in
git. `bd ready` is where to start; `bd prime` prints the full agent workflow.

- Capture: `bd create --title="…" --description="…" --priority 2 -l <area>`
- Pick: `bd ready` then `bd update <id> --claim` — never start unclaimed work.
- Hand off: label the bead `needs-merge` with the commit and PR in `--notes`,
  after `eng/verify.sh` is green on the branch. Formatting is not a hand-off
  step any more and `--full` never was; CI and an occasional
  `--bead <id> --full` cover what a scoped run does not (ADR-0134).
- Merge and complete: `python3 tools/bd-merge-bead.py --bead <id>` — never a
  hand-rolled `git merge` + `bd close`. It fetches, rebases onto `origin/main`,
  runs the fast gate (`eng/verify.sh --fast`) on the rebased branch, merges,
  **pushes**, and only then closes, and only after
  `git merge-base --is-ancestor <merge> origin/main` passes. A green
  `eng/verify.sh` on the *branch* is not a close gate: a branch is not what the
  next worker starts from, a merge left unpushed makes `origin/main` — the
  base every worktree is branched off — miss work that is already merged, and
  the merge gate names its lane explicitly so a runner's `CI=true` cannot turn a
  three-minute merge into a twenty-minute one (ADR-0118, as amended by
  ADR-0134; SpatialEngine-xbz, SpatialEngine-u2x.51).
  `--bead <id> --full` gates that one merge on the exhaustive lane instead,
  and `--skip-tests <substring>` leaves a named suite to CI on that merge — it
  prints what it dropped and writes it into the close reason. After the close
  it archives the bead's workspace and the agent that held it, so a finished
  bead leaves no worktree behind; a paseo that cannot do it is a warning, not a
  failed merge, and `--no-archive-workspace` keeps the workspace (a reopened
  bead, say).
  `--check` is the top-of-tick gate (is `main` published?),
  `--publish` pushes it, and `--audit` finds closed beads whose work never
  reached `origin/main`.
- Complete: `bd close <id> --reason="…"` — only after CI's run for that merge
  is green on `main`, never on the branch. The fast gate is never the only
  thing that ran.
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
  `architecture/decisions/` win on conflict. `arch-index.md` is the
  breadcrumb, and `architecture/decisions/README.md` is the index of every
  record with its status, date and cross-references — both generated from the
  records by `python3 tools/arch-index.py --write`, and every lane of
  `eng/verify.sh` fails if the committed copy is stale (ADR-0141).
