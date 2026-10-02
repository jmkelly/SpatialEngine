---
status: accepted
date: 2026-09-30
deciders: maintainer + agent
summary: The three verification lanes are implemented: `eng/verify.sh` builds and runs the test projects that reach the change, `--format` scopes `dotnet format` to the projects that own the changed files, and `--full` is the flat gate; `tools/verify_scope.py` computes the scope and refuses to guess, failing loud to the whole solution. The contract these lanes implement is decided in ADR-0118.
---

# ADR-0109: the verification lanes, implemented

**This record is the implementation of a decision taken elsewhere. The decision
is ADR-0118** — "the default verify lane is a build gate, and the full lane is
enforced at the merge" — and where the two disagree about the gate contract,
ADR-0118 wins. This record does not decide anything; it says what was built for
it and what the build cost.

An earlier draft of this file was titled "the default verification lane is a
build gate, and CI runs the full one", and read as the decision itself. It is
not: an ADR written by the implementer of a gate change is a proposal about
the repo's documented gate, and SpatialEngine-lyz's own acceptance criteria
asked for the opposite shape (no-arg = full, `--quick` = scoped). ADR-0118 is
the maintainer's authorisation of the shape actually built, and it is what the
rest of the repository cites. What follows is the engineering content that
ADR-0118 refers to as "SpatialEngine-lyz's implementation record".

## Context

`eng/verify.sh` was one flat run — `dotnet format --verify-no-changes` over the
solution, `dotnet build`, `dotnet test` over the solution, the python tooling
tests — and every change had to pass it before it was done. Agents run it
constantly, under a coordinator fanning out eight workers onto one 12-core box,
and on that box it costs ~39 minutes cold and ~25 warm.

Almost none of that is signal. Measured in worktree `1mmcart7` (warm NuGet
cache, Docker up):

| Step | Wall time |
| --- | --- |
| `dotnet restore` | 65 s |
| `dotnet format --verify-no-changes` | 677–786 s |
| `dotnet build` | 149 s |
| `dotnet test --no-build` | 1366 s (`Spatial.Host.Tests` alone, 21 m 49 s) |
| python tooling unittests | 67 s |

The format step is ~30% of the run, and the usual first reading — the analyzers
are slow — is wrong: `--diagnostics IDE0055` over the whole solution takes
625 s, no better than the default. The cost is loading every project through
MSBuild, and that cost is per project: one project warm is 43–47 s, so a
two-project branch is ~90 s of formatter against ~700 s of solution-wide work.
On the test side, a `dotnet test` with an empty filter over all 55 projects
costs 19 s of launch, so the wall time is the suites themselves and almost none
of it is harness: `Spatial.Host.Tests` is 21 m 49 s of the 1366 s, and the 19
unit projects together are 77 s on 12 cores.

The two obvious deletions are not available. The build cannot replace the
formatter: with `EnforceCodeStyleInBuild` and `TreatWarningsAsErrors` an
injected trailing-whitespace/IDE0055 violation still builds green, so
`dotnet format` is the only formatter in the repo. And the suite that dominates
the run cannot be skipped, because it is the one that gates everything.

What the measurement actually says is that the cost is *where the work runs*,
not *whether it runs*. The expensive half — the whole-solution formatter, the
22-minute container suites, the entire test matrix — is exactly the half that
does not change when a developer touches one file, and it is also the half a
GitHub runner does better than a box with eight agents on it. A default gate
that an agent runs on every save does not have to carry the merge-time signal:
that signal can move to the merge.

## Decision

What ADR-0118 decides, restated here as built: `eng/verify.sh` keeps its name
and gains lanes. What each one runs, and who runs it:

| Lane | Runs | Who |
| --- | --- | --- |
| `eng/verify.sh` (default; `--quick` is a synonym) | build, plus the test projects that reach the change over `ProjectReference`, plus `Spatial.Architecture.Tests`; the python tooling tests when `tools/**` moved | every agent, every iteration |
| `eng/verify.sh --format` | `dotnet format --verify-no-changes` on the projects that own the changed files (~45 s each) | before a bead is handed off |
| `eng/verify.sh --full` | format over the whole solution, build, every test project, the python tooling tests | the coordinator on the rebased branch before a merge; CI on every pull request and after every merge to `main`; locally on demand |
| `eng/verify.sh --plan` | print the lane's steps, run nothing | the tests, and anyone checking what a run would do |

**The default is the build gate**, because that is what gets run twenty times a
day and it should feel like a save. It does not format and it does not run the
whole suite.

**`CI=true` selects `--full` unless a lane is named.** The bare name is the
build gate, so the next workflow someone writes that calls it without thinking
would otherwise get the scoped lane where it meant the merge gate. A named lane
always wins, which is what lets the split CI jobs run the two halves of `--full`
without either of them picking up the other.

**Before a hand-off an agent runs the default lane and `--format`, and nothing
else** — not `--full`, which is 15–25 minutes on a contended box and is the
merge gate's job, not the agent loop's.

**Formatting is not dropped, it moves.** It is still enforced on every pull
request and on `main` after the merge, where a failure is a red check rather
than an agent's memory; and `eng/verify.sh --format` keeps it available as a
cheap pre-handoff step, because ~45 s per changed project is a price worth
paying before a handoff. The formatter is one formatter: scoped
`dotnet format`, not a second tool with a second style.

**The test scope is the change set.** `tools/verify_scope.py` walks
`git diff --name-only <base>...HEAD` plus the dirty working tree, maps the
changed files onto the projects that own them, and takes the test projects that
reach those projects over the `ProjectReference` graph — transitively, so a
change to `Spatial.Core` still runs the host suite through two hops.
`Spatial.Architecture.Tests` always runs: 35 s, and a change that quietly moved
a project onto a third-party dependency is exactly what a fast lane would let
through.

**The lane refuses to guess.** When the change set cannot be read (an
unresolvable base, no `origin/main`) or the branch changes a file no single
project owns (a root `Directory.Packages.props`, `.editorconfig`, the solution
itself), the plan comes back `exhaustive` and every lane falls back to the
whole solution. A scoping failure costs time; a silent under-run costs a defect
in `main`.

**CI splits the heavy half onto its own runner.** `.github/workflows/ci.yml`
runs the formatter, the build, the non-container test projects and the
python tooling tests in `verify`, and `Spatial.Host.Tests`,
`Spatial.PostGIS.Tests` and `Spatial.SqlServer.Tests` in a new `integration`
job on its own box. The two lanes partition the test projects exactly —
`LaneTests` proves that against the real solution rather than trusting the
YAML — and `e2e` waits on both. Together
they are what `eng/verify.sh --full` runs, so a pull request gets the full
signal and `main` gets it again after the merge. What CI is *not*, today, is a
gate: `main` has no branch protection and its verify job is red on every push
(ADR-0118 §4; SpatialEngine-ivp, SpatialEngine-bv4). Until those two hold, the
full lane is enforced by the coordinator's pre-merge `--full` run and CI is a
detector.

### The split is read off what a suite needs, not off how long it ran (2026-09-30)

The lane split above was originally a hand-kept list, and the fourth entry in
it was `Spatial.Ingest.Codec.Tests` — a project under `tests/unit/` that was
measured at 2m27 while this record was written and put in the container lane
because of it (SpatialEngine-lyz, SpatialEngine-0dx). It is a unit suite.
Measured on the 12-core host on 2026-09-30: 103 cases in 8 classes, 3–6 s warm
across three runs and 17 s from a cold `obj/` including the build, against 77 s
for the other 18 unit projects combined. It takes no `Testcontainers` package
and no `ProjectReference` beyond `Spatial.Ingest.Codec` and `Spatial.Core`,
and there is not one `File.`, `Directory.`, `HttpClient`, `Socket`,
`Process.Start` or `Testcontainers` in its 1,424 lines: it opens nothing
outside the process.

The cost was a per-test fixture, not I/O. `A_large_geojson_upload_streams_instead_of_materialising`
ran a `Task.Run` sampler that spun on `GC.GetTotalMemory` once a millisecond
for the length of two 40,000-feature decodes, and the byte ceiling it read was
a statement about the machine — 77 MB streamed against 96 MB buffered on the
CI runner, green everywhere else — because the sampler counted every byte
another test in the assembly was allocating. SpatialEngine-ivp replaced it with
a reachability bound, which needs no sampler and no machine, and the suite has
run in seconds since.

So the list is gone. `verify_scope.lane_projects` classifies a test project by
its own `PackageReference`s and its folder: a suite is in the integration lane
if it lives under `tests/integration/` or takes a `Testcontainers*` package.
The dependency is the thing that puts a Docker daemon on the runner, so it is
the thing that decides the lane; a wall-clock measurement is a property of the
host and of this week's fixtures, and a list built from one re-derives itself
after every unrelated change to a suite. `LaneTests` now holds the codec suite
in the unit lane, holds every integration-lane project to a `Testcontainers*`
dependency, and holds a container-backed suite under `tests/unit/` in the
integration lane — so the rule cuts both ways rather than only loosening the
one entry this bead was about.

`eng/verify.sh --plan` prints the steps a lane would run and runs nothing,
which is how the contract is tested (`ScriptLaneTests` runs the real script
against a throwaway repository with a known change set) without spending a lane.

### On a faster whole-repo formatter

The cheap win was enough: scoped `dotnet format` is ~45 s per project against
~700 s for the solution, which fits a pre-handoff step and needs no new
dependency, no second style and no whole-repo reformat diff. A genuinely faster
formatter (`csharpier` via `.config/dotnet-tools.json`, sub-minute on this
repo) is therefore **not** evaluated here and **not** adopted: adopting one
means a whole-repo reformat diff and a style decision, which is its own bead
with its own ADR. The measurement, the style-divergence blast radius and the
licensing/pinning story belong there; the reasoning for not doing it here is
recorded in SpatialEngine-4h0. That evaluation is now written up in
`research/formatting/csharpier-evaluation.md`, and it recommends the same answer:
3-10x faster and sub-minute, against a 92%-of-files, ~87,000-line reformat and
four gate call sites — and a merge gate that, since ADR-0134, runs no formatter
at all.

### A project-level `.editorconfig` is scoped, and formattable

The two spellings of `.editorconfig` reach the lane by two different arms, and
both have to be matched on the file's **name**: `Path('.editorconfig').suffix`
is `''`, because pathlib reads a leading-dot filename as having no extension,
so no suffix test can ever match either one.

The root file reaches every project, so `_is_solution_wide` names it and the
plan comes back `exhaustive` (SpatialEngine-x8p). A `.editorconfig` under
`src/<project>/` is that project's own, so it stays scoped — but it still has
to select that project's format check, and until 2026-10-02 the formattable
arm tested `Path(f).suffix` while `FORMATTABLE_SUFFIXES` carried the literal
`'.editorconfig'`. The constant named a file the test could not match, so a
branch that added or edited a project-level `.editorconfig` selected **no**
format projects and `--format` ran no formatter over the project whose style
had just changed. The gate was green on a change it had not read.

`_is_formattable` now tests the suffix *or* the name, and the split is pinned
from both sides: the root spelling falls back to exhaustive, the below-the-root
one selects its own project (SpatialEngine-148).

## Consequences

- The default lane costs minutes instead of ~25, and scales with the size of
  the change rather than the size of the solution.
- A `--quick`-style lane is no longer the only thing between a change and
  `main`, but a human could still hand off on a default run and skip the format
  step. That risk is answered in the three places an agent reads before
  handing off — `AGENTS.md` (Commands, and the Hand off/Complete bullets),
  `eng/swarm-runbook.md` (worker steps 5–6 and the Notes) and the header of
  `eng/verify.sh` — and it is bounded rather than removed: the merge gate runs
  the formatter on `main` either way, so a violation that slips past a handoff
  turns the post-merge CI run red rather than reaching `main` unseen.
- The scoping is a second thing to keep true, and it has to be right in both
  directions. `tools/test_verify_scope.py` covers both: a broken test in a
  *changed* project must be caught, and a broken test in an *unchanged* project
  must not run. The second case is the one that would look like a win, which is
  why it is a test.
- The default lane is not free of the suites it skips. A change to
  `Spatial.Core` legitimately pulls in `Spatial.Host.Tests` and costs 22
  minutes. That is correct under-running, and the fix is not to weaken the
  closure — the follow-on beads for the flaky host timeouts
  (SpatialEngine-c5f) and for the 2 m 27 s unit codec suite (SpatialEngine-0dx)
  are where that cost is attacked.
- Two CI jobs means two builds of the solution on GitHub's minutes, and the
  integration job is the one that can be slow or flaky, because it is the one
  that starts containers. A flake there is a flake the flat gate had too.
- `dotnet format` does not flag trailing whitespace inside comments, although
  `.editorconfig` sets `trim_trailing_whitespace = true` for all files. Measured
  on this SDK: a code line with trailing whitespace fails the check with exit 2,
  the same violation on a comment line passes with exit 0, and the fix run does
  not touch it either. This is pre-existing, it is not a scoping artefact, and
  it is a hole in the merge gate too — captured in SpatialEngine-emo, not fixed
  here, because fixing it is a formatter/analyzer decision rather than a gate
  one.

## References

- ADR-0118 — the decision these lanes implement, and the authority for the
  gate contract
- `eng/verify.sh`, `tools/verify_scope.py`, `tools/test_verify_scope.py`
- `.github/workflows/ci.yml` (`verify`, `integration`, `e2e`)
- `AGENTS.md` ("Commands"), `eng/swarm-runbook.md` (worker steps 5–6, Notes)
- `tests/architecture/Spatial.Architecture.Tests/AdrNumberingTests.cs` (why
  this record's number was reserved before it was written, ADR-0089)

## Measurements

Taken 2026-09-29 in this worktree on the 12-core host, **while it was running
other agents**. The load average moved between 5 and 83 over the session, and
the sub-step numbers below are from the runs at each end of that range, so
read them as a range rather than a point. `Spatial.Host.Tests` also had 10
unrelated HTTP-copy timeout failures in the measurement the bead was written
from (SpatialEngine-c5f); the `--full` run below was green, so the full-lane
figure is what the gate costs when it behaves.

| Lane | Scenario | Wall time | Host load |
| --- | --- | --- | --- |
| default | two-project branch, green (build 41 s + architecture 22 s + one unit suite 0.6 s) | **1 m 19 s** | 21 |
| default | same branch with a broken test in a changed project | 5 m 01 s, exit 1 | 82 |
| `--format` | two projects | 1 m 45 s (~50 s per project) | 8–21 |
| `--full` | everything: format ~2 m 45 s, build 42 s, every test project, tooling tests | **15 m 18 s**, exit 0 | 38–60 |

The default lane against the flat gate is 1 m 19 s versus 15 m 18 s on the
same box, and the two are measured the same way. The 22-minute host suite is
the floor under both: the flat gate pays it whether or not the branch touched
the host, and the default lane pays it only when the change reaches the host
through `ProjectReference`.

For the scoping itself, two runs on a two-project branch with a deliberately
broken test: **red**, with the failure named in the changed project's suite
(`Failed! - Failed: 1 ... Spatial.Maps.Tests.dll`), and then **green** with a
deliberately broken test still sitting in an unchanged project's suite
(`Spatial.Stores.Memory.Tests`) that scoping did not select.

The formatter comparison SpatialEngine-4h0 measured on 2026-10-01, same box,
same command line, interleaved at load 11-53: `csharpier` 1.3.0 checks the whole
repository in 18.5-37.7 s wall / 1 m 53 s-2 m 44 s CPU against 3 m 02 s wall /
11 m 22 s CPU for `dotnet format SpatialEngine.slnx --verify-no-changes
--no-restore`, and would rewrite 966 of the 1046 files it reads (+64,507 /
-22,639 lines). The rewrite is mechanical — no token but a comma moves, no
runtime string value changes, and the reformatted tree builds the solution with 0
warnings — and it is still a repository-wide diff, which is what the ADR above
declines. The 677-786 s above did not reproduce on a quieter box: the same
whole-solution command cost 2 m 19 s-8 m 39 s wall there, so read the older
figure as the contended end of that range.

The merge gate was proven to catch a formatting violation, not assumed: with a
code-line whitespace violation injected, the whole-solution `dotnet format`
that CI runs reported `error WHITESPACE: Fix whitespace formatting. Delete 2
characters` and exited 2, and the scoped format lane did the same on that
project. The hole in that gate — the same violation on a *comment* line passes
— is pre-existing and is SpatialEngine-emo.
