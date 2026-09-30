---
status: accepted
date: 2026-09-30
deciders: maintainer + agent
---

# ADR-0134: the merge gate is the fast lane, and formatting leaves the merge path

## Context

ADR-0118 tiered `eng/verify.sh` into lanes and kept the flat gate at the merge:
`eng/verify.sh --full` — format over the whole solution, build, every test
project — run by `tools/bd-merge-bead.py --bead <id>` on the rebased branch, so
no bead closed without the whole suite green behind it (SpatialEngine-u2x.51).
That was a defensible reading of "a close has to be justified" and it is the
wrong one at this swarm's rate.

The swarm does **hundreds of merges a day**. A `--full` run is 15–25 minutes
warm on the 12-core host, under a load average that was measured at 93 while
this record was written. So the merge gate cost the swarm three to six hours of
gate time per day, and what it bought was re-running the 22-minute
`Spatial.Host.Tests` and the other container-backed suites for a branch that
touched `tools/` or `Spatial.Tiling.WebMercator`. The signal was in the scoped
part; the cost was almost entirely in the unscoped part. And a gate nobody can
run in a reasonable time is a gate people route around, which is the failure
mode ADR-0118 was written to prevent.

The maintainer's instruction (SpatialEngine-6g7) is explicit about the trade:
**as fast as possible, some safety traded away, formatting run once in a while
rather than on every merge, and a few minutes at most for the verify and the
merge together.**

What the merge gate actually protects is narrower than `--full` made it sound.
`tools/bd-merge-bead.py` closes a bead only after
`git merge-base --is-ancestor <merge> origin/main` passes on a freshly fetched
origin (SpatialEngine-xbz). *That* is the gate that loses work when it is
skipped: the publish half. The verify half is a defence against a red branch
reaching `main`, and ADR-0118 §4 already says what covers what the pre-merge
run cannot know: CI on `main`, which runs the full suite after the merge.

## Decision

**The merge gate is `eng/verify.sh --fast` — the scoped lane — and `dotnet
format` is not on the merge path at all.** This amends ADR-0118 §2 ("the full
lane is enforced pre-merge by the coordinator, as `eng/verify.sh --full`") and
its §1 table row for `--full`. Everything else in ADR-0118 stands: the tiering,
the `CI=true` backstop, the loud fallback when scoping fails, the exhaustive
lane as a real lane, and the flake rule.

### 1. The lanes after this record

| Lane | Runs | Who runs it |
| --- | --- | --- |
| `eng/verify.sh --fast` (no arguments and `--quick` are synonyms) | a **scoped** build — a generated `.verify-scoped.slnx` holding the changed projects and the test projects that reach them — then the test projects that reach the change over `ProjectReference`, plus `Spatial.Architecture.Tests`; the python tooling tests when `tools/**` moved | every agent, every iteration, **and the merge** |
| `eng/verify.sh --format` | `dotnet format --verify-no-changes` over the projects owning the changed files | occasionally, and CI — not before every hand-off |
| `eng/verify.sh --full` | format over the whole solution, build, **every** test project, the python tooling tests | `--bead <id> --full` when a change is broad enough to doubt the scoping; CI on every pull request and after every merge to `main` |

`tools/bd-merge-bead.py` names the lane explicitly (`--fast`, or `--full` when
asked). That is not decoration: under ADR-0118 a bare `eng/verify.sh` under
`CI=true` runs the exhaustive lane, so an unnamed invocation lets a runner's
environment decide what a merge costs. `--verified` (spelled `--full-verified`
in older runs) still declares a gate run the operator did by hand, and skips
nothing but the run itself.

### 2. The scoped build, because MSBuild loads rather than compiles

The fast lane already scoped which *tests* ran and still paid a whole-solution
build, because `dotnet build SpatialEngine.slnx` evaluates all 50 projects.
Measured warm on the swarm's host under load:

| Build | Time |
| --- | --- |
| `dotnet build SpatialEngine.slnx` (no changes at all) | 1 m 07 s |
| the same, scoped to three projects | 8 s |
| a single leaf test project | 9 s |

So the fast lane generates `.verify-scoped.slnx` from the plan
`tools/verify_scope.py` already prints — the changed projects *and* the test
projects that reach them, because a scoped build that only ran tests would
never compile a project nothing references — builds it, and then runs
`dotnet test` **once** over that file instead of one `dotnet test` process per
project. The per-process MSBuild load was paid once per suite before; it is
paid once now. The file is deleted on the way out, including on failure: a
stale scoped solution in the repository root is a gate that keeps building
yesterday's change set.

`tools/verify_scope.py` also stopped mapping only *formattable* files onto
projects. That was the right question for `--format` and the wrong one for what
to compile: a changed `.json` fixture beside a project has to select that
project. Mapping every changed file is a superset of the old behaviour, so it
can only add tests, never drop them.

### 3. What was traded, named

- **A red test in an unchanged project can reach `main`.** The merge gate now
  covers the tests the change reaches, not every test in the repository. CI on
  `main` catches it after the merge rather than before it, and `main` has no
  branch protection (ADR-0118 §4), so it is caught rather than blocked.
- **A formatting violation can reach `main`.** `dotnet format` is CI's and the
  occasional `--format` run's job. ADR-0118's own consequences note that the
  formatter does not even flag trailing whitespace inside comments, so the
  lane being dropped from the merge path is a smaller loss than it looks
  (SpatialEngine-emo).
- **`bd close` is no longer justified by "every test passed".** It is justified
  by the scoped gate plus the ancestor check on `origin/main`, and the reason
  `bd close` writes says which lane ran.
- Nothing was traded on the publish half. The fetch, the unpublished-`main`
  refusal, the rebase conflict abort, the red-gate abort, the interrupted-run
  abort and the `git merge-base --is-ancestor` check all stay: they cost
  seconds and they are what loses work when skipped.
- The merge tool takes `--skip-tests <substring>` (repeatable), which leaves a
  named suite to CI on that merge. It is off by default, it prints what it
  dropped, it writes the dropped names into the `bd close` reason, and it
  cannot drop `Spatial.Architecture.Tests`: a merge that leaned on CI says so
  in the record rather than looking identical to one that did not. With the
  timings below it is not needed for a leaf change; it is there for a
  `Spatial.Core` merge on a day when the container suites are the whole cost.

### 4. Keeping the scoped lane honest

Two directions, and the one that looks like a win is the one that needs tests:

- the under-run guard — a broken test in a changed project must run — is
  unchanged, and gained a case for the non-code file that used to select
  nothing;
- the *new* under-run direction is the scoped build: a changed project nothing
  references must still compile, which is why `build_projects` is the changed
  projects and not just the test projects.

`tools/verify_scope.py` asserts both against the real script (through
`--plan`, and through the generated scoped solution's own contents), that every
reference in the *real* repository resolves, that a change to a project the
solution omits still selects its tests, and that a `--skip-tests` pattern is
loud and cannot take the architecture guard.
`tools/test_bd_merge_bead.py` asserts the lane the merge tool names, that the
lane is always named, that the exhaustive lane is opt-in, that a named skip
reaches both the lane and the close reason, and that every refusal from
ADR-0118 still aborts the merge.

## Consequences

- The fast gate and a `--bead <id>` merge are minutes: a tooling or docs change
  is about one, a leaf `src/**` change about two and a half, and the widest
  closure there is — a change under `Spatial.Core` — about seven and a half,
  all with every affected suite green and nothing skipped. Against the 15–25
  minutes the merge gate cost before, at hundreds of merges a day, that is the
  difference between a gate people run and a gate people route around.
- `Spatial.Architecture.Tests` still runs on every lane, so the structural
  guard — no contract grew a dependency it should not have — is not among the
  things traded.
- A change under `Spatial.Core` still pulls in the host, PostGIS and SQL Server
  suites through the reference closure, so that merge is ~7½ minutes rather
  than ~1. That is the scoping working, it is the honest cost of a change that
  touches everything, and it is what `--skip-tests` and `--bead --full` are
  for. SpatialEngine-c5f and SpatialEngine-0dx are the beads for two of the
  suites behind it.
- A gate that is cheap enough to run is one nobody routes around. The expensive
  part of ADR-0118's contract — "an agent does NOT run `--full`" — stops being
  a rule people have to be told.
- The exhaustive lane is now *opt-in* rather than *the gate*, so nothing runs
  it automatically on a developer's box before a merge. CI runs it on every
  pull request and after every merge, which is where ADR-0118 §4 already wanted
  the real enforcement.

## Supersedes and references

- ADR-0118 §1 (the `--full` row) and §2 (the pre-merge `--full` requirement);
  ADR-0109's lane description. Everything else in both stands.
- `eng/verify.sh`, `tools/verify_scope.py`, `tools/test_verify_scope.py`,
  `tools/bd-merge-bead.py`, `tools/test_bd_merge_bead.py`, `.gitignore`
- `AGENTS.md` ("Commands", "Task queue"), `eng/swarm-runbook.md` (steps 2 and 5)
- `.github/workflows/ci.yml` (unchanged: CI still runs the whole thing)
- SpatialEngine-6g7 (this record's bead); SpatialEngine-xbz, SpatialEngine-u2x.51

## Measurements

Taken 2026-09-30 on the swarm's 12-core host with other agents running; load
average 93 while the first three were taken, so read them as upper bounds. Every
`eng/verify.sh --fast` figure is end to end — scoped build, affected tests,
python tooling tests — run in the bead's worktree, and green.

| Question | Measurement |
| --- | --- |
| What does a whole-solution build cost when nothing changed? | `dotnet build SpatialEngine.slnx`, 1 m 07 s, load 93. |
| What does the same build cost scoped to three projects? | 8 s — the difference is MSBuild loading 50 projects, not compiling. |
| What does a single leaf test project cost to build and test? | 9 s build, 11 s `dotnet test` (411 tests, 1 s of them). |
| What do the affected suites cost in one invocation? | `dotnet test` over the three-project scoped solution: 50 s wall, of which 46 s is `Spatial.Architecture.Tests`. The same three as three separate processes: ~78 s. |
| **Fast gate, tooling + docs change** (this branch) | **1 m 04 s**, green: one architecture suite (84 tests) and the 151 python tooling tests. |
| **Fast gate, a `src/Spatial.Tiling.WebMercator` change** | **2 m 40 s**, green, nothing skipped: 4 affected suites, including `Spatial.Host.Tests` at 790 tests in 1 m 54 s. |
| **The same change with `--skip-tests=integration`** | **1 m 09 s**, green — the skip is what a coordinator reaches for on a contended box, and it names the suite it dropped. |
| **Fast gate, a `src/Spatial.Core` change — the widest closure** | **7 m 32 s**, green, nothing skipped: 26 projects built, 25 suites, ~6 500 tests. |
| What does the gate this replaces cost? | `--full` is 15–25 minutes warm (ADR-0118), and 20 m 57 s measured under load 48–52. |
| What did the scoped lane actually run before this record? | One suite. The reference graph was empty (§2a), so a `src/**` change selected `Spatial.Architecture.Tests` and nothing else. |
