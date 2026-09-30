---
status: accepted
date: 2026-09-30
deciders: maintainer + agent
summary: Every lane that runs `dotnet test` reads the per-suite skip counts back out of the run and fails a suite that skipped most of what it was asked to run.
---

# ADR-0139: a mass-skipped suite is a red lane, not a green one

## Context

`dotnet test` exits 0 when nothing failed, and that is the only thing
`eng/verify.sh` read. The container-backed suites do not fail when their
fixture cannot start: `SqlServerContainerFixture` and the PostGIS equivalent
turn "the Docker daemon was not reachable" into `Skip.If(!_fixture.DockerAvailable,
…)` on every case that needs the container, deliberately — a missing socket is
an environment problem and a red wall of container tests reads as a broken
change.

On 2026-09-30 that combination merged. SpatialEngine-u2x.58 existed to fix the
two SQL Server conformance tests; its fast gate reported
`Spatial.SqlServer.Tests` as **10 passed / 104 skipped** and exited 0, and
`tools/bd-merge-bead.py` merged it (ADR-0134 made the fast lane the merge
gate). The two conformance tests were among the 104. The coordinator noticed by
hand afterwards and re-ran the class standalone, 4/4 — but nothing in the gate
enforced that, and the same thing happens again on the next contended merge.

The tell is in the run, and it is why the case is decidable rather than
ambiguous: `Spatial.Host.Tests` and `Spatial.PostGIS.Tests` green while
`Spatial.SqlServer.Tests` skips 91% of itself is container contention under
parallel worktrees, not a missing daemon. The gate has all of that
information and was not reading any of it. The runbook had grown a human-scale
instruction — "re-run the affected class standalone before treating the green
as one" — which is a workaround for a gate that cannot see the defect
(SpatialEngine-8lj).

## Decision

**Every lane of `eng/verify.sh` that runs `dotnet test` also reads the per-suite
skip counts back out of the run and fails the lane when a suite in the run's
scope skipped most of what it was asked to run.** The read is
`tools/skip_gate.py`, over the trx files `dotnet test --logger trx
--results-directory .verify-test-results` writes.

### 1. What is read, and what trips it

The trx `Counters` carry `total`, `passed`, `failed` and `notExecuted`, and
`notExecuted` is what the schema calls a skipped case. The rule is a
**per-suite ratio with a floor**:

- more than `VERIFY_SKIP_RATIO` (default **0.5**) of a suite's tests skipped, **and**
- at least `VERIFY_MIN_SKIPPED` (default **10**) of them.

Both halves are load-bearing. The ratio alone would fail a suite with one
conditional case in a small project, and a count alone would let a big suite
skip a third of itself. A per-suite ratio rather than a run-wide one, because
the case that matters is one suite's skips against the other suites' passes: a
run-wide ratio over ~6 500 tests dilutes 104 skips into 1.6% and passes. The
floor is what keeps the threshold from becoming noise the swarm learns to route
around, which is the failure mode ADR-0118 was written to prevent (ADR-0118
§3: a gate people route around is worse than no gate).

Both numbers are readable from the environment, so a suite with a genuinely
conditional case is moved over the line loudly rather than by editing the gate.

### 2. A suite whose result file went missing is not a pass

The trx file name is stamped to the second (`<machine>_<user>_<date>`), so two
projects finishing in the same second can overwrite one another, and ADR-0134's
single `dotnet test` over the scoped solution is exactly the run where that can
happen. So the gate is told how many suites the run was asked to run —
`--expect` — and **fewer result files than that is an ERROR**, not a pass. A
suite that vanished is the same invisibility as a suite that skipped.

### 3. `--skip-tests` stays the only way to run a suite absent

A suite dropped by `--skip-tests` is out of the run, so it is out of `--expect`
and is never judged here. That is the opt-out ADR-0134 §3 already defined, it
is loud, and it is recorded in the bead's close reason. No second escape hatch
is added: a threshold that is routinely overridden is not a threshold, and the
opt-out that exists already is recorded in the place a reader looks.

### 4. Cancellation, failure and the ordinary green run

A mass-skip **fails** the lane rather than printing a warning, because the gate
is what merges (`tools/bd-merge-bead.py` reads its exit code) and a hard ERROR
in a run whose exit code is 0 is a note in a log. A green run prints its counts
and exits 0. Cancellation is unchanged: a lane interrupted at the test step
still exits non-zero, because `set -e` stops it before the read and the read is
the last step.

### 5. What this does not change

- `Spatial.Architecture.Tests` and the other cheap suites are unaffected: they
  skip nothing, and the floor means a conditional case in a large suite is
  still a pass.
- CI's split jobs spell out their own steps (`.github/workflows/ci.yml`) and do
  not call this; wiring the read into them is CI's own change and is not
  pretended here.
- ADR-0118 §3's flake rule is untouched — a red lane, like a red test, is
  re-run once and a red twice is a stop condition. A red *lane* on skips has the
  obvious remedy: an idle box, or `--skip-tests=<substring>` with CI on `main`
  covering it.

## Consequences

- The false green that merged SpatialEngine-u2x.58 is a red lane the next time.
  The coordinator no longer has to notice a skip count in a log, which is the
  part that was never going to hold at hundreds of merges a day.
- The cost is the trx logger and one python process per lane run — seconds, and
  measured on the tooling lane below. No extra `dotnet test` invocation, so
  ADR-0134's one-invocation trade is untouched.
- A red lane on skips is new, so it will be met with surprise. That is why §5
  keeps the remedy explicit: contention is not a defect in the branch, and
  `--skip-tests` is the honest way to record that CI is carrying that suite for
  that merge.
- The floor (10) is a judgement, and it is the one number to revisit if a suite
  legitimately mass-skips on a machine that is not contended. It is an
  environment variable rather than a constant in the gate for that reason.

## Supersedes and references

- Nothing is superseded. This adds a condition to ADR-0134's merge gate and to
  ADR-0118 §1's lanes, and it does not change what either runs.
- `eng/verify.sh`, `tools/skip_gate.py`, `tools/test_skip_gate.py`,
  `tools/test_verify_scope.py` (the pinned plan steps), `.gitignore`,
  `AGENTS.md` ("Commands"), `eng/swarm-runbook.md` (step 3 and step 7)
- SpatialEngine-8lj (this record's bead); SpatialEngine-u2x.58, the merge that
  exposed it; SpatialEngine-c5f, the distinct HTTP-copy timeout bug this bead
  is *not*

## Measurements

Taken 2026-09-30 in the bead's worktree, `eng/verify.sh --fast` on this
branch — a `tools/**` and `eng/**` change, so the lane's scope is the
architecture guard plus the python tooling tests, which is the cheapest
closure and therefore the honest place to price the new step.

| Question | Measurement |
| --- | --- |
| Does one `dotnet test` over a two-project scoped solution give each project its own trx file, and can they be told apart? | Yes: `_omarchy_2026-09-30_22_48_32_net10.0.trx` (56 tests) and `…_22_48_34_net10.0.trx` (411 tests); the assembly is read off the test-name namespace, and `notExecuted` off the `Counters`. |
| What does the trx logger cost? | Two extra arguments on the `dotnet test` the lane already runs, and 80 KB + 556 KB of trx for the 467 tests of the two-project probe; one python process reading them is under 0.1 s. |
| **Fast gate, this change (tooling + docs + the gate)** | **1 m 23 s**, green: `Spatial.Architecture.Tests` (84 tests, 0 skipped), 173 python tooling tests, and `== skips: 1 suite(s) reported, 0 over the threshold (more than 50% and at least 10 skipped) ==`. |
