---
status: accepted
date: 2026-09-30
deciders: maintainer + agent
summary: The no-unresolved-conflict-marker rule is **checked by `tools/conflict_markers.py`, and every lane runs that check** — as a check the lanes call directly, not as a `tools/test_*.py` the tooling suite happens to discover, because both `eng/verify.sh` and `tools/verify_scope.py` run the tooling suite only when the change set touches `tools/**`, so the merge gate (the fast lane since ADR-0134) never read the rule on a change that did not, and the marker this rule exists for arrived on exactly that change: a `CHANGELOG.md` merge (`9429422`, SpatialEngine-u2x.37) whose change set was docs and `src` with no `tools/` file in it. CI caught it after the merge to `main`, which is the ADR-0143 hole in the same shape and a week later. The check scans every tracked file, reports `path:line: text`, exits 1, changes nothing and costs 1.2 s; a rule that runs only when `tools/**` changed is not a gate.
amends: ADR-0134, ADR-0143
---

# ADR-0146: the conflict-marker rule is a check every lane runs, not a test `tools/**` happens to run

## Context

`tools/test_no_conflict_markers.py` was the rule that no tracked file may carry
an unresolved merge-conflict marker, and it was true of the repository: a
`<<<<<<< HEAD` line had reached `CHANGELOG.md` on main and rendered as
changelog text (SpatialEngine-u2x.37's `9429422`, judged in SpatialEngine-aot),
and CI's `verify` job caught it on the run that follows the merge
(.github/workflows/ci.yml:88).

The merge gate did not. Two independent reasons, and only one of them is
interesting:

1. The rule was written as a `tools/test_*.py`, so `python3 -m unittest
   discover --start-directory tools` finds it — but both `eng/verify.sh:386` and
   `tools/verify_scope.py:355` run the tooling suite only when `run_python_tooling`
   is set, which is `any(f.startswith("tools/") for f in changed_files)`. So
   the fast lane skipped the one check that would have caught the marker on any
   change set that touches no tool file.
2. The marker arrived on exactly such a change set: `git show 9429422 --stat`
   is docs and `src`, and the file it landed in is prose the build never reads.

So the fast gate was green, the bead closed on that green, and CI caught the
defect *after* `main` had it. That is ADR-0143's hole in the same shape and a
week later: a repository-wide claim that nothing on the merge path read.

ADR-0143's answer — a check the lanes call directly, next to
`tools/trailing_whitespace.py`, rather than a test a lane happens to discover —
is taken here unchanged, and this record is the "does it want its own ADR?"
question the bead left open answered as yes: it amends what ADR-0134 and
ADR-0143 put in every lane, it changes the merge gate's cost, and it is a rule
about the repository rather than about a project's code.

The bead named the alternative and rejected it: keep it a test and widen the
tooling gate to every lane. That is worse in the exact way this record exists
to fix — the tooling suite is 100 s (measured, 222 tests) and growing, and it
would buy, on every merge, a rule that costs 1.2 s, on the strength of a suite
whose *composition* changes for unrelated reasons.

## Decision

**`tools/conflict_markers.py` is the check, and every lane calls it.**

### 1. What it is, and what it is not

A check and not a formatter: it reads one rule, reports `path:line: text`,
exits 1, changes no file, and has no `--fix`. It needs no .NET SDK, so it runs
in a checkout with no toolchain — which is what lets the CI `verify` job own it
as one line, exactly as ADR-0143's check does.

The matching itself was moved out of `tools/test_no_conflict_markers.py` into
the script, so there is **one** implementation of the rule: a test carrying a
second copy of the matching can pass while the check every merge runs fails.
The markers are still assembled from character runs rather than written
literally, so the two files — both tracked, both describing the markers they
forbid — do not trip their own rule.

The scope is deliberately blunt and is inherited unchanged: **every tracked
file**, from `git ls-files --cached`, not the files a merge touched. A marker
is a property of a merge rather than of the file it resolved, the historical one
is a one-character-class orphan with no partner, and narrowing the sweep to the
change set would narrow the very blind spot this bead is about. It is a
repository check and not a check on the change, so unlike the whitespace step
it has no scoped variant and no "unreadable change set" fallback — there is
nothing to narrow.

### 2. Where it runs

Every lane, after the trailing-whitespace check and before the doc gate —
first because it is cheap and a marker is not worth an eleven-minute format
step, and in that order only because both are repo checks that precede the
expensive part of a lane (ADR-0143, ADR-0141).

| Lane | What it runs |
| --- | --- |
| `--fast` (the merge gate) | the check, unconditionally — **not** inside the `tools/**`-only tooling gate |
| `--format` | the check |
| `--full` | the check, and the tooling suite as before |
| CI `verify` | the check as its own step, plus the tooling suite |

CI's `verify` job spells its steps out rather than calling the script — the
deliberate exception ADR-0134 describes — so it carries the step explicitly.
Wiring the lanes alone would have left the detector that follows every merge
with the same hole one level up.

### 3. The tests that are about the gate, not the checker

`tools/test_no_conflict_markers.py` keeps the unit tests over the matching
(sourced from the script) and gains `LaneWiringTests`, in the shape ADR-0143
established:

- every lane calls `conflict_marker_step`, and the helper still runs
  `tools/conflict_markers.py` — a checker nothing calls is the same invisible
  hole one level up;
- the call in the fast lane stands **before** `if [[ "$RUN_TOOLING" == "1" ]]`
  and not inside it, which is the regression itself: a step inside that block
  is the defect this bead is about, in one assertion;
- the check run as a command over the real repository exits 0, and exits 1 with
  a `CHANGELOG.md:1` report over a throwaway repository holding the historical
  orphan;
- CI still runs it.

The existing lane-plan tests in `tools/test_verify_scope.py` and the lane
fixture in `tools/test_skip_gate.py` were updated to carry the new step and the
new tool, so a lane that dropped it would be a red test rather than a comment.

## Consequences

- The fast lane now reads the conflict-marker rule on **every** merge, and the
  shape of the marker that reached `CHANGELOG.md` is caught *before* the merge
  rather than by CI on `main` afterwards.
- The merge gate pays **1.2 s** for it (measured over 3166 tracked files), and
  the tooling gate stays exactly as conditional as it was — the check is not
  routed through a 100 s suite to buy a one-second rule.
- The rule is now stated once, in the script the lanes call, rather than in a
  test's docstring; `tools/test_no_conflict_markers.py` keeps the history and
  the unit tests.
- The check is blind to an untracked file, by construction: `git ls-files
  --cached` is what the repository ships. A marker written into a working file
  that is never committed is not a merge artefact.
- It remains blind to the case ADR-0143's sibling judgement named: a merge
  whose sides had both edited the file can drop a hunk and leave *no* marker.
  Nothing here can see that; it is told apart by the merge, not the file, and
  SpatialEngine-aot is the record of judging it.

## References

- `tools/conflict_markers.py`, `tools/test_no_conflict_markers.py`,
  `eng/verify.sh` (`conflict_marker_step`), `.github/workflows/ci.yml`,
  `tools/test_verify_scope.py`, `tools/test_skip_gate.py`
- `AGENTS.md` ("Commands"), `eng/swarm-runbook.md` (step 6)
- ADR-0134 (the merge gate is the fast lane, and what it traded), ADR-0143 (the
  check-not-a-formatter shape, and the "a checker nothing calls" hole), ADR-0141
  (lane composition: the repo checks before the expensive part)
- SpatialEngine-14y (this record's bead); SpatialEngine-u2x.52 (the rule as it
  stood), SpatialEngine-aot (the merge judgement that found the gate gap),
  SpatialEngine-u2x.37 (the commit and merge that carried the marker)

## Measurements

Taken 2026-09-30 in the bead's worktree, over 3166 tracked files.

| Question | Measurement |
| --- | --- |
| What does the check cost over the whole repository? | 1.2 s per run (`git ls-files --cached`, every file read as text). |
| What did the tooling suite cost, and what would routing the rule through it buy? | ~100 s for the 222 tests it discovers — which is the argument for a direct step rather than a wider tooling gate. |
| Does the fast lane run the check on a change set with no `tools/` file? | It did not before this record: the rule was inside the `RUN_TOOLING` conditional (eng/verify.sh:386). `test_the_fast_lane_does_not_gate_it_on_tools_changing` pins the fix. |
| What does the check cost on the historical marker? | The same 1.2 s, plus one `CHANGELOG.md:1: <<<<<<< HEAD` line and exit 1. |
