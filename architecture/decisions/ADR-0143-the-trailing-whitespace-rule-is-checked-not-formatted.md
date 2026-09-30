---
status: accepted
date: 2026-09-30
deciders: maintainer + agent
---

# ADR-0143: the trailing-whitespace rule is checked, not formatted

## Context

`.editorconfig` sets `trim_trailing_whitespace = true` for `[*]`, and
`dotnet format` — the only formatter in this repository, and the whole of what
`eng/verify.sh --format` and the CI `verify` job run for whitespace — enforces
it over part of the files that break it. Measured on the SDK this repository
pins (10.0.400) by injecting one violation at a time and running
`dotnet format --verify-no-changes` (SpatialEngine-emo):

| Where the whitespace is | What the formatter says |
| --- | --- |
| a line carrying code | `error WHITESPACE: Fix whitespace formatting. Delete 2 characters`, exit 2 |
| a **comment-only** line, mid-file | nothing, exit 0 |

The same asymmetry holds at solution scope, and `dotnet format whitespace`
*without* `--verify-no-changes` leaves the comment-line whitespace in place
rather than trimming it, so it is not a report-only quirk either. The
repository therefore asserted a rule in its configuration that its gate
enforced over a subset of the files, and the subset the formatter skipped was
invisible to every lane: the exhaustive gate and the CI job were as blind to it
as the scoped one. It is pre-existing, and it is not a scoping artefact — a
scoped and a solution-wide run behave identically, which is what makes it a
hole in the merge gate rather than in the merge *lane*.

ADR-0134 traded a formatting violation reaching `main` for the merge gate being
minutes rather than a quarter of an hour, and its own consequences note named
this bead as the reason that trade was smaller than it looks. It is a real
hole, and the fix has to be measured: whatever goes into a lane is paid by
every PR and by every post-merge run, hundreds of times a day.

The bead named three shapes: a Roslyn analyzer implementing IDE0055 over
comment trivia, an `.editorconfig`/formatting change, or a check that is
independent of the formatter.

## Decision

**`tools/trailing_whitespace.py` checks the rule, and every lane runs it
first.** It is a check and not a second formatter: it reads one rule, reports
`path:line: trailing whitespace (n characters)`, exits 1, and changes no file.
There is no `--fix`.

### 1. Why a check and not an analyzer

An analyzer is the better shape if the rule ever grows — it runs in the build
and reports with the rest of the diagnostics. It is not that here for a
measured reason: ADR-0109 established that solution-wide `dotnet format` costs
~700 s and that the cost is MSBuild loading 50 projects, not analysis, and the
bead's own measurement is that a single project is ~25 s of formatter before it
has looked at a file. A rule that costs two seconds belongs on the merge path;
the same rule implemented as a build-time analyzer does not. The check is also
independent of the .NET SDK, so it runs in a checkout with no toolchain at all
— which is what lets the CI `verify` job own it as one line.

The check is a **superset** of the formatter's signal, not a different one: a
code line is reported by both, a comment-only line only by this. Both cases
are pinned in `tools/test_trailing_whitespace.py`.

### 2. What it reads from `.editorconfig`, and what it does not

It reads the **scope** of the rule, never the rule itself. A file whose most
specific matching section sets `trim_trailing_whitespace = false` is not
reported, with editorconfig's own ordering (last matching section wins, a
pattern with no `/` matched against the file name, one with a `/` against the
path from the root). `[*.md]` does exactly that here and is load-bearing: two
trailing spaces at the end of a markdown line are a hard line break, so a check
that ignored the section would fail every prose wrap in the documentation.

Two files were found in the repository by turning the check on, and both are
captured tool output rather than something anyone typed: `psql` pads its column
headers and rules to the terminal width, and that padding is the record of what
the tool printed. They are exempted by a section in `.editorconfig` with the
reason in it, rather than by retyping a capture. The check does not read
`insert_final_newline`, `indent_size` or anything else: one rule, and no growth
into a second formatter nobody measured.

### 3. Where it runs

Every lane, and first in every lane, because it costs about two seconds and a
violation reported after an eleven-minute format step is eleven minutes
wasted.

| Lane | What it reads |
| --- | --- |
| `--fast` (the merge gate) | the changed files, from the same plan the lane already computes |
| `--format` | the changed files |
| `--full` | every file the repository ships |
| CI `verify` | every file the repository ships |

The scoped lanes pass the change set through `tools/verify_scope.py --list
plan --machine`, which gained a `files:` key beside `format:`, `build:` and
`tests:` — a comment line belongs to no project as far as the formatter is
concerned, so the change set has to be readable in its own right. An unreadable
change set falls back to the whole repository, which is the same fallback the
rest of a lane makes and costs the same two seconds either way. A deleted file
is dropped rather than reported: the change set names it and there is nothing
in it to read.

CI's `verify` job spells its steps out rather than calling the script — the
deliberate exception ADR-0134 describes — so it carries the step explicitly.
Wiring the lanes alone would have left the detector that follows every merge
with the same hole.

### 4. The two tests that are about the gate, not the checker

- `tools/test_trailing_whitespace.py::RepositoryTests` runs the check over the
  real repository, so the gate is red on a violation committed by anyone.
- `LaneWiringTests` asserts that each of the three lanes still calls the check
  and that the helper still runs the tool, and that CI still runs it. A checker
  nothing calls is the same invisible hole one level up, and this is the second
  time a claim in this repository has been true only of the files something
  read (the conflict-marker gap, `tools/test_no_conflict_markers.py`).

## Consequences

- The rule in `.editorconfig` is enforced over every file it names. A
  comment-only violation now fails the merge gate and CI, where before it
  reached `main` silently.
- The exhaustive lane and the CI `verify` job each pay **~2 s** for the check
  over the whole repository, and the merge gate pays under 0.1 s for the change
  set — against a 15–25 minute `--full` and a three-minute fast gate. The
  marginal cost is not measurable in the lane timings, which is the whole reason
  this shape was chosen over the analyzer.
- It runs in a checkout with no .NET SDK, so the python tooling tests and this
  check cover whitespace on a machine that cannot build the solution.
- A pre-existing violation anywhere in the repository is now a red `--full` and
  a red CI run until it is fixed or exempted. There were three, and all three
  are either markdown or captured `psql` output.
- The two file types the check does not read are still the formatter's: with
  `EnforceCodeStyleInBuild` off (ADR-0109), a code-line violation is caught by
  the build, and every other whitespace rule in `.editorconfig` is unenforced
  outside a `--format` run. This is one rule, not the formatter's job.

## References

- `tools/trailing_whitespace.py`, `tools/test_trailing_whitespace.py`,
  `eng/verify.sh`, `tools/verify_scope.py`, `.editorconfig`,
  `.github/workflows/ci.yml`
- `AGENTS.md` ("Commands"), `eng/swarm-runbook.md` (step 6)
- ADR-0109 (what the formatter costs), ADR-0118 as amended by ADR-0134 (the
  lanes and the merge gate), ADR-0134 §3's second consequence, which named
  this bead
- SpatialEngine-emo (this record's bead); SpatialEngine-lyz, whose format-lane
  proof found it

## Measurements

Taken 2026-09-30 in the bead's worktree, SDK 10.0.400.

| Question | Measurement |
| --- | --- |
| Does the formatter report a comment-only violation? | No. `dotnet format src/Spatial.Tiling.WebMercator/…csproj --verify-no-changes`, two comment-only lines with trailing spaces, exit 0, no output, 24 s. |
| Does it report the same violation on a code line? | Yes. `error WHITESPACE: … Replace 5 characters with '\n\n'`, exit 2. |
| Does `dotnet format whitespace` without `--verify-no-changes` fix it? | No — the whitespace is still there afterwards. |
| What does the check cost over the whole repository? | 1.9 s clean, 2.5 s reporting one violation, over `git ls-files --cached --others --exclude-standard`. |
| What does the check cost over a 6-file change set? | under 0.1 s. |
| What did the repository contain? | 2 files with trailing whitespace, both `psql` plan captures under `eng/spike-u2x-query-baseline/results/`, and 1 markdown file — the latter two categories now exempt through `.editorconfig`. |
