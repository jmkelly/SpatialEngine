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
- The host is JIT-compiled.
- Long-running work is a cancellable `Task`; failures are structured
  `SpatialException` codes (`invalid.arguments`, `not.found`,
  `store.unavailable`).
- Behaviour changes land contract, SDK and test updates together, with
  success, failure and cancellation tested. Test-first: every defect or gap
  gets a failing reproduction test before the fix, and the fix is confirmed by
  that test passing. Pin package versions in `Directory.Packages.props`.

## Commands

The gate is `eng/verify.sh`, and only `eng/verify.sh`: it builds and runs the
tooling tests too, so it is what "before done" means here. Everything else here
reports *into* it or is a hand-off, never a substitute: `eng/e2e-web.sh` and
`eng/workbench-e2e.sh` are separate evidence for a change that reaches a
delivered client, and the `quality-loop` skill's five audits and
`eng/quality-audit.sh` are reporting lanes whose queues sit beside the build.
Take this as the answer whenever another file or another agent offers another.

- `eng/verify.sh` — the **fast gate**, and the default: builds the projects
  this branch's change reaches (a generated scoped solution, not all fifty) and
  runs the test projects that reach it over `ProjectReference`, plus
  `Spatial.Architecture.Tests` (and the python tooling tests when `tools/**`
  changed). Minutes rather than a quarter of an hour, and it is the lane the
  merge tool runs.
- `eng/verify.sh --full` — the **exhaustive gate**: format over the whole
  solution, build, every test project, the python tooling tests. Opt in per
  merge (`bd-merge-bead.py --bead <id> --full`) when a change is broad enough to
  doubt the scoping; CI runs it on every pull request and again on `main` after
  the merge.
- `eng/verify.sh --format` — the format lane, `dotnet format
  --verify-no-changes` scoped to the projects owning the changed files (~45 s
  each, against ~700 s for the whole solution). Deliberately off the merge
  path: formatting is enforced by CI and by an occasional run rather than paid
  for hundreds of times a day, so it is not a hand-off step.
- `eng/verify.sh --fast` — the same as no arguments, named so `CI=true`
  cannot promote a merge to the exhaustive lane. `--skip-tests <substring>`
  (repeatable, or `VERIFY_SKIP_TESTS`) leaves a named suite to CI on one run;
  it prints what it dropped and cannot drop `Spatial.Architecture.Tests`.
- `eng/verify.sh --plan` prints what a lane would run and runs nothing. Every
  lane also runs the repository's own checks: `trailing_whitespace.py`,
  `final_newline.py`, `conflict_markers.py`, `doc_surface.py`,
  `spike_harnesses.py` (every `eng/` csproj the solution
  does not name is built by every lane, since nothing else compiles it),
  `package_agents.py`, `skip_gate.py` (`VERIFY_SKIP_RATIO` 0.5 and
  `VERIFY_MIN_SKIPPED` 10 fail a suite that ran but skipped most of what it was
  asked to, and a suite dropped by `--skip-tests` is out of the count rather
  than judged) and `beads_gate.py` (a closed bead's merge records
  the bead, and an open
  bead whose agent `paseo` still reports *running* is a finding; a
  run that can read neither the queue nor `paseo` — CI — judges those as *not
  judged*, and `--strict` fails instead).
  It also runs `tools/changelog.py --check`, which fails on a hand-merged
  `## [Unreleased]` section: there is none, because a release section is
  generated from the `Task:` trailers and the narratives the work commits
  already carry, once per release (`RELEASING.md` step 3).
- `CI=true` with no lane named selects `--full`, so a workflow that calls the
  bare script gets the exhaustive gate rather than the fast one.
- `bd` — the development task queue (capture, claim, status). Run `bd prime`
  for the full agent workflow.
- `python3 tools/swarm_lock.py` — the swarm's file-overlap lock, derived from
  what each ready bead names in its own description rather than hand-listed
  in the runbook: `--bead <id>` gates one bead on it
  and `metrics` counts the mis-dispatches and WIP commits a drain cycle
  produced. Before taking a bead that another worker is already on, run
  `--bead <id>`.
- `eng/e2e-web.sh`, `eng/workbench-e2e.sh` — real host + delivered clients.
- `eng/seed.sh` — on-demand realistic dataset: fetch public data, ingest
  (with engine-side reprojection) and publish styled feature/map services.
- Quality loop: read the `quality-loop` skill first — it is the five code audits
  (CRAP, coverage, metrics, Stryker, warnings), and by the definition above it
  reports into `eng/verify.sh` rather than replacing it, so a red audit queue is
  something to drain there, not a second verdict on the branch. Two repo policy
  files decide what those audits report: `.dependably` — coupling and
  complexity rules plus the grandfathered exceptions; open it when a metrics
  finding reads as a judgement rather than a defect. `coverage-policy.json` —
  the branch-coverage floor and what counts as authored; open it before
  arguing about a coverage delta. `eng/quality-audit.sh` aggregates
  the code audits with the documentation audit (`tools/doc-freshness.py`)
  into one answer, and every lane runs it with `--report`:
  it fills `doc-queue.md` / `doc-report.json` and cannot fail a lane.

## Task queue

Work is tracked in [beads](https://github.com/gastownhall/beads) (`bd`), not in
git. `bd ready` is where to start; `bd prime` prints the full agent workflow.

- Capture: `bd create --title="…" --description="…" --priority 2 -l <area>`
- Pick: `bd ready` then `bd update <id> --claim` — never start unclaimed work.
- Hand off: label the bead `needs-merge` with the commit and PR in `--notes`,
  after `eng/verify.sh` is green on the branch. Formatting is not a hand-off
  step any more and `--full` never was; CI and an occasional
  `--bead <id> --full` cover what a scoped run does not.
- Merge and complete: `python3 tools/bd-merge-bead.py --bead <id>` — never a
  hand-rolled `git merge` + `bd close`. It fetches, rebases onto `origin/main`,
  runs the fast gate (`eng/verify.sh --fast`) on the rebased branch, merges,
  **pushes**, and only then closes, and only after
  `git merge-base --is-ancestor <merge> origin/main` passes. A green
  `eng/verify.sh` on the *branch* is not a close gate: a branch is not what the
  next worker starts from, and a merge left unpushed makes `origin/main` — the
  base every worktree is branched off — miss work that is already merged.
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
  expires — never bare `bd reclaim`, which keys on lease age alone and so cannot
  tell a crashed agent from one that simply has not heartbeated.
  The wrapper cross-checks the stale set against
  `paseo ls` — the bead's `bd/<id>` worktree, or the `agent <id>` its notes
  recorded at claim time — and reclaims only the beads no live agent holds,
  reporting each one it leaves in place. `--dry-run` reports the verdicts and
  reaps nothing; every lane also reports a lease that went while its agent was
  still running, so read the gate's finding before reclaiming
  anything by hand. A worker can renew its own lease mid-bead with
  `python3 tools/bd-safe-reclaim.py --heartbeat <id>` (a plain `bd heartbeat
  <id>` does the same). Keep in-flight work
  on a WIP commit rather than in the stash: the stash is shared across this
  repository's worktrees and has clobbered a worker mid-task.

Areas are labels and route through `architecture/principles.md`. The
database lives in the **git common dir** (`.beads/` beside the shared `.git`),
so every worktree sees one queue — `bd where` prints the resolved path. It is
local coordination state, not history: beads versions it in Dolt, and commit
provenance goes in the bead plus the `Task: <id>` commit trailer.

Migration note: the previous `eng/tasks` SQLite queue was replaced by beads on
2026-09-27. `tools/migrate-tasks-to-beads.py` is the one-shot mapping (status,
priority 1..5 → 0..4, area → label, agent/branch/commit → notes) and is kept
only as the record of that mapping.

## Detail on demand

- Architecture, contracts, host, Esri, ingest, rendering — the standing
  shape is `architecture/principles.md`; the code is the documentation.
- In-flight state is `bd`, not a document in the repository root: no root file
  answers "what is happening now" (`tools/doc_surface.py`, every lane).
  `docs/CHANGELOG.md` is a release artefact rather than a context
  source, and its release sections are generated from the history
  (`tools/changelog.py`) — for what changed on a path, read
  `git log -- <path>` and the bead.
