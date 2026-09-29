---
status: accepted
date: 2026-09-30
deciders: maintainer + agent
---

# ADR-0118: the default verify lane is a build gate, and the full lane is enforced at the merge

## Context

`eng/verify.sh` was one flat run — `dotnet format --verify-no-changes` over
the solution, `dotnet build`, `dotnet test` over the solution, the python
tooling tests — and `AGENTS.md` named it, in one line, as *the gate before
done*: format, build, full tests. On the swarm's 12-core host that run costs
~39 minutes cold and ~25 warm, and agents run it constantly, eight workers deep
on one box.

SpatialEngine-lyz set out to tier it, and its bead asked for the shape that
keeps the documented contract: `--quick` is the scoped inner loop, **no
arguments is the full gate unchanged**, CI gains a job for the heavy suites. The
branch as it stands ships the inverse. No arguments is now a scoped build gate,
the format check moved behind `--format`, and the full lane moved behind a new
`--full`, with `AGENTS.md`, `eng/swarm-runbook.md` and the branch's own
implementation record rewritten to match. The hand-off note claimed the no-argument contract was unchanged; it was
not.

That divergence is why this record exists. An ADR written by the implementer of
a gate change is a *proposal* about the repo's documented gate, not the
authorisation to redefine it, and the bead's own acceptance criteria asked for
the opposite shape. The work is green and good. The open question is the
contract, and it is a decision, not another implementation pass.

Two pieces of evidence decide how much the answer can lean on CI, and both were
measured on 2026-09-30 against the live repository, not read off the branch.

**Nothing blocks a merge.** `gh api repos/jmkelly/SpatialEngine/branches/main/protection`
returns `Branch not protected`, and `…/rulesets` returns `[]`. There is no
required status check, so CI cannot *prevent* anything; it can only report
afterwards. The coordinator merges straight to `main` with a local run as the
gate, which makes the merge gate a process step rather than a platform one.

**Main's CI verify job is red on every push.** The last eight `ci` runs on
`main` are all `failure`, and the same test is in all of them:
`Spatial.Architecture.Tests.AdrNumberingTests.The_check_gate_refuses_a_number_a_live_reservation_holds`,
expecting exit 1 and getting 0. It passes in every local checkout. A second,
machine-dependent failure appears in some runs:
`Spatial.Ingest.Codec.Tests.StreamingDecodeTests.A_large_geojson_upload_streams_instead_of_materialising`
— "streaming peaked at 77,136,936 bytes against 96,486,280 buffered" on the
runner, green here. Because `e2e` has `needs: [verify]`, the end-to-end job has
been **skipped** on every one of those runs: the host, the SDK drift check, the
browser workbench and the CLI have not run in CI at all during this swarm.

So "the full lane is enforced in CI" is, today, a detector that fires on
everything, attached to a merge nothing gates. And main was red in the local
gate too while this record was being written: the `SpatialEngine-u2x.21.2` merge
renumbered the tile-composition record from ADR-0116 to ADR-0117 in its message
and changelog but not in the file name, so two records claimed 0116 and
`AdrNumberingTests` failed on `main` — and the merges after it went ahead
anyway. Red `main` is currently a state the swarm merges through, not a state
that stops it.

The maintainer's answer to the shape question is on the record, in the
SpatialEngine-lyz notes, revised 2026-09-29:

> REVISED 2026-09-29 by human: the default lane becomes a FAST BUILD GATE, not
> the full gate. eng/verify.sh (no args) = restore + build + affected tests
> only, run constantly by agents. Format moves OFF the default path … The FULL
> run … becomes the POST-MERGE gate on CI after merge to main; PR CI still runs
> it, so nothing goes unenforced — enforcement moves to the merge, not the agent
> loop.

That is the authorisation this record was asked to find, and it is a human
decision about the repo's own gate. What it does not do is make the enforcement
real, and the two measurements above are why the record has to say how.

## Decision

**The default lane may stop being the full gate.** The default lane is a scoped
build gate, the full lane is `eng/verify.sh --full`, and the full lane is
enforced at the merge. ADR-0118 supersedes the no-argument-is-the-full-gate
contract in SpatialEngine-lyz's original text, and it is the authority for
SpatialEngine-lyz's implementation record (it holds 0109 on
`bd/SpatialEngine-lyz` and is renumbered at merge, so it is named rather
than cited here): where the two disagree about the gate contract, this one
wins.

### 1. The contract, stated once

| Lane | Runs | Who runs it |
| --- | --- | --- |
| `eng/verify.sh` (no arguments; `--quick` is a synonym) | restore, build, the test projects that reach the change over `ProjectReference`, `Spatial.Architecture.Tests` always; the python tooling tests when `tools/**` moved | every agent, every iteration |
| `eng/verify.sh --format` | `dotnet format --verify-no-changes` scoped to the projects owning the changed files | before handing a bead off |
| `eng/verify.sh --full` | format over the whole solution, build, **every** test project, the python tooling tests | the coordinator before a merge; CI on every pull request and after every merge to `main`; locally on demand |
| `eng/verify.sh --plan` | prints the lane's steps and runs nothing | tests, and anyone checking what a run would do |

The scoping fails loud, not quiet: an unresolvable base, or a change to a file
no single project owns (`.editorconfig`, `Directory.Packages.props`,
`SpatialEngine.slnx`, `global.json`), makes the plan exhaustive and every lane
falls back to the whole solution.

Two clauses exist because the default lane no longer carries the signal:

- **`CI=true` selects `--full` unless a lane is named explicitly.** A workflow
  that calls a bare `eng/verify.sh` must not silently become the scoped lane.
  The split CI jobs are the deliberate exception: they are two boxes running one
  gate, so they spell out the steps they each own, and a test proves the two
  together are exactly what `--full` runs rather than trusting the YAML. The
  `CI=true` default is the backstop for the next workflow someone writes that
  does not think about it.
- **Every document that says "run `eng/verify.sh`" as *the gate* names
  `--full`.** The bare name now means the fast lane. The places that carry the
  old sentence: `AGENTS.md` (Commands; Hand off; Complete), `README.md`,
  `RELEASING.md`, `HANDOFF.md`, `.pi/skills/spatial-engine/SKILL.md`,
  `.pi/skills/spatial-engine/scripts/doctor.sh`,
  `.pi/skills/refactor/SKILL.md`, and `eng/swarm-runbook.md`. A doc that is
  left saying "the gate" over a bare invocation is a defect in that doc.

### 2. What runs before a hand-off — one rule

Before labelling a bead `needs-merge`, an agent runs the default lane and
`eng/verify.sh --format`, and nothing else. It does **not** run `--full`: that
lane is 15–25 minutes on a contended box, it is not signal at that point, and
requiring it would put the swarm back where it started.

The full lane is not an agent's step. It is the merge gate, and it is enforced
in two places, both of which must be written down in the same breath:

- **pre-merge, by the coordinator**, as `eng/verify.sh --full` on the rebased
  branch — explicitly `--full`, never the bare script, because under this
  contract the bare script is the build gate and using it here would make the
  scoped lane the merge gate;
- **post-merge, by CI on `main`**, which is the detector for anything the
  pre-merge run could not have known — and, today, a detector that is red on
  everything (see Consequences).

`bd close` keeps its existing condition, restated against the new lanes: a
bead closes when the full lane is green on `main`, which today means the
post-merge `ci` run for that merge is green, not that some branch was.

### 3. Where the full suite is enforced, given a flaky suite

A red pre-merge `--full` blocks the merge. That is the point, and it is not
relaxed because the suite is flaky: `SpatialEngine-c5f` (ten Host.Tests
HTTP-copy timeouts under parallel load) and the runner-sensitive codec test
above are real and are being fixed on their own beads. The rule for the flake
is the runbook's existing one, applied to the full lane: **re-run `--full` once
on the same commit; green merges; red twice for the same reason is a stop
condition** — stop, do not merge, do not spawn around it. "Probably a flake" is
never a reason to merge a red full lane, because the pre-merge run is the only
place the full signal is checked on this repository.

A red `ci` run *after* a merge is fixed **forward**, not by reverting: the
pre-merge `--full` was green, so the commit is not known-bad, and a revert
throws away a branch whose local full lane passed. The coordinator records it
as a P1 bead, stops merging into the affected area, and lets the next tick
drain it. Red `main` accumulating unnoticed is what let the duplicate ADR-0116
survive four merges.

### 4. What CI has to become before it can be called the gate

Stated as a condition rather than a fait accompli, because both halves are
maintainer actions and neither is in the repository:

1. `ci` on `main` is **green** — the two runner-sensitive failures above are
   fixed, so a red run means something;
2. `main` has **branch protection with the `verify` (and `integration`, `e2e`)
   jobs as required status checks**, so a red run blocks the merge instead of
   annotating it.

Until both hold, the honest description of the enforcement is: the full lane is
enforced by the coordinator's pre-merge `eng/verify.sh --full`, and CI is a
detector. The decision does not depend on them — the tiering is worth having
either way — but the claim "nothing goes unenforced" does.

## Consequences

- The default lane costs minutes instead of ~25 and scales with the size of the
  change, not the size of the solution. The measured ratio is 1 m 19 s against
  15 m 18 s on the same box, same method, both under load.
- The scoped lane is a second thing to keep true, in both directions, and the
  direction that would *look* like a win is the one that needs a test: a broken
  test in an unchanged project must not run. `tools/test_verify_scope.py` is
  where that lives, and `ScriptLaneTests` must assert it against the real
  script rather than against the scoping helper.
- A change to `Spatial.Core` still pulls in the 22-minute host suite through the
  closure. That is correct under-running, and it is what SpatialEngine-c5f and
  SpatialEngine-0dx are for.
- The hand-off rule is weaker than the old one by exactly the distance between
  "every iteration" and "the merge". It is bounded — the format lane runs
  before every hand-off, and the full lane runs before every merge — but it is
  bounded by a process step, not by a platform gate, and that is the honest
  risk this record accepts.
- `dotnet format` does not flag trailing whitespace inside comments although
  `.editorconfig` sets `trim_trailing_whitespace`. Pre-existing, present in the
  merge gate too, and SpatialEngine-emo.
- Adopting a second formatter (csharpier) is **not** authorised here: whole-repo
  reformat, second style, its own record. SpatialEngine-4h0.

## Supersedes and references

- SpatialEngine-lyz's original design (no-arg = full, `--quick` = scoped). Its
  implementation record, whatever number the merge gives it; this record is
  the authorisation it was missing.
- `eng/verify.sh`, `tools/verify_scope.py`, `tools/test_verify_scope.py`
- `.github/workflows/ci.yml`; `eng/swarm-runbook.md` (merge step, worker steps)
- `AGENTS.md` ("Commands", "Task queue")
- ADR-0089 (why this record's number was reserved before it was written)

## Measurements

Taken 2026-09-30 for this record, on the swarm's 12-core host with other agents
running; load average was 7–30 over the session, so read them as upper bounds.
The lane timings themselves are SpatialEngine-lyz's, measured 2026-09-29 in its
worktree at load 21–82.

| Question | Measurement |
| --- | --- |
| Can CI block a merge? | `branches/main/protection` → `Branch not protected`; `rulesets` → `[]`. No required checks. |
| Is CI green on `main`? | Last 8 `ci` runs on `main`: 8 failures. `AdrNumberingTests.The_check_gate_refuses_a_number_a_live_reservation_holds` in all 8 (exit 0 where 1 was expected; green in every local checkout). `StreamingDecodeTests` machine-dependent memory ceiling in 2 of 8. `e2e` `skipped` in all 8 (`needs: [verify]`). |
| Is the local gate green on `main`? | No: `AdrNumberingTests` failed on `main` — ADR-0116 claimed twice, the `u2x.21.2` renumber applied to prose and not to the file name. Fixed in the commit that carries this record. |
| What does the tiering buy? | Default lane 1 m 19 s vs `--full` 15 m 18 s, same box, same method. `--full` green, exit 0. |
| What does the flat gate cost under swarm load? | `eng/verify.sh` (the flat gate, before this record's tiering) run to land this record: **20 m 57 s, exit 0**, load 48–52, peaking near 170 during the PostGIS suite. `Spatial.Host.Tests` 779/779 green in 8 m 39 s — the c5f timeouts did not reproduce at this load. This is the cost an agent pays on every save, and it is why the default lane is not it. |
