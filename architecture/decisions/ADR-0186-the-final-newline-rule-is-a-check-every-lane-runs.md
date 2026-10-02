---
status: accepted
date: 2026-10-01
deciders: maintainer + agent
summary: The `insert_final_newline` the `.editorconfig` claims for `[*]` is **checked by `tools/final_newline.py`, and every lane runs that check** — a sibling of `tools/trailing_whitespace.py`, not a second rule inside it, because ADR-0143 closed that door deliberately ("no growth into a second formatter nobody measured") and the repository's shape for a *new* repository rule is a second script wired the same way (ADR-0146). The check exists because ADR-0134 took `dotnet format` off the merge path and left a C# file with no final newline with no detector between the edit and `main`: measured at `origin/main` `eb909b5c` on 2026-10-01, a clean tree exited 2 from `dotnet format SpatialEngine.slnx --verify-no-changes` with 22 violations in 16 files, 14 of them this rule, and CI was the only reader — on the far side of the merge. It reads `.cs` only, because `FINALNEWLINE` is a Roslyn diagnostic and a `.csproj` with no final newline is measured exit 0; the rest of `[*]` (about 190 files git ships, mostly captured fixtures and prose nothing formats) is a separate bead. About 2 s, no .NET SDK, no `--fix`.
amends: ADR-0134, ADR-0143
related: ADR-0109, ADR-0146
---

# ADR-0186: the final-newline rule is a check every lane runs

## Context

`.editorconfig` sets `insert_final_newline = true` for `[*]`, and `dotnet
format` does enforce it on C# source — `error FINALNEWLINE: Fix final newline.
Insert '\n'`, exit 2. But since ADR-0134 the formatter is **not on the merge
path**: `eng/verify.sh --fast` builds and tests and does not format, and
`tools/bd-merge-bead.py --bead` runs the fast lane. So the only thing in the
repository reading that rule is CI's `verify` job, which runs after the merge to
`main`.

Measured 2026-10-01 at `origin/main` `eb909b5c`, SDK 10.0.400, on a clean tree
(SpatialEngine-744): `dotnet format SpatialEngine.slnx --verify-no-changes`
exited 2 with **22 violations in 16 files**, and 14 of them were this one rule —
files ending in `}` with no newline after it. Every one was byte-identical on
`main`, landed by five merges on 2026-10-01 (`785338a5`, `a4732083`, `7b0a50bc`,
`5e2b9619`). So ADR-0109's consequence claim — that a formatting violation
turns the post-merge CI run red — was true of `main` itself rather than of a
slip, and `main`'s formatter step is red today.

That is a real hole and the trade is one ADR-0134 named. This record closes the
one class of it that is cheap to close, and names the part that is not.

## Decision

**`tools/final_newline.py` checks the rule and every lane runs it**, beside
`tools/trailing_whitespace.py`, reporting `path: no final newline`, exiting 1,
changing nothing, with no `--fix` and no .NET SDK.

### 1. A sibling script, not a second rule in the whitespace check

ADR-0143 §2 was explicit: "The check does not read `insert_final_newline`,
`indent_size` or anything else: one rule, and no growth into a second formatter
nobody measured." A second rule inside that check would contradict its own
record and its acceptance criteria, and the cheapest way to keep a check from
becoming a second formatter is to keep it to the rule it was measured for. A
second *script*, wired the way ADR-0146 wired the conflict-marker check, is the
shape this repository already uses for every repository rule that arrived after
ADR-0143. ADR-0143 §2's own consequence ("every other whitespace rule in
`.editorconfig` is unenforced outside a `--format` run") was a true statement
about the state, not an argument against ever closing it.

### 2. Which files it reads is measured

`FINALNEWLINE` is a Roslyn diagnostic, so it is C# source: `dotnet format
clients/dotnet/Spatial.Client/Spatial.Client.csproj --verify-no-changes` exits
**0** on a project file with no final newline, where the same violation in a
`.cs` file exits 2. So this check reads `.cs` and only `.cs`.

The `.editorconfig` claim is `[*]`, and reading all of it is **not** in scope
here: the same sweep finds **about 190** files git ships with no final newline,
most of them captured `psql` and Esri fixtures, generated snapshots, ADR prose
and `.editorconfig` itself. A gate that goes red on 190 findings cannot be
landed in one commit, and a gate that stays red is a gate that gets switched
off. That is a project with its own exemptions (the ADR-0143 `psql` capture
precedent is the shape it will take), and it is filed as **SpatialEngine-3rz**
with the per-category list. The
cost of that choice is named rather than hidden: **the `[*]` claim stays
unenforced outside `dotnet format` for everything that is not C#.**

The `.editorconfig` *scope* is read rather than assumed — a file whose most
specific matching section sets `insert_final_newline = false` is not reported —
so an exemption added later is honoured instead of demanded back.

### 3. Where it runs, and what it costs

Every lane, immediately after the trailing-whitespace check, and in CI's `verify`
job beside it. Scoped lanes pass the change set and the exhaustive lane reads
the repository, which is `whitespace_step`'s shape for `whitespace_step`'s
reasons: it is the detector rather than a check on a branch, and an unreadable
change set falls back to the whole repository rather than to a guess. The
pinned half is `tools/test_final_newline.py`: the repository ships no
violation, and each lane and CI still call the check — a checker nothing calls
is the same invisible hole one level up (ADR-0146's argument, seconded).

The cost is ADR-0143's measured one, because the code is that check's: about two
seconds over the repository, under 0.1 s over a change set, no SDK. That is
what makes this affordable to run hundreds of times a day where the formatter
(~$700 s solution-wide, ADR-0109; ~45 s per project scoped, ADR-0134) is not.

### 4. What is fixed here, and what stays the formatter's

The 20 C# files without a final newline on this branch are fixed, and so is
every other violation the solution-wide run reported: four switch expression
arms and one object initialiser over-indented, one import ordering, and three
more whitespace violations in files that landed after the bead's measurement
(`src/Spatial.Transformations.ProjNet/Grids/GridShiftCandidate.cs`,
`tests/unit/Spatial.Rendering.Skia.Tests/TestFixtures.cs`,
`tests/unit/Spatial.Transformations.ProjNet.Tests/PublishedGridDeploymentTests.cs`
— each a statement and its brace left on one line). `dotnet format
SpatialEngine.slnx --verify-no-changes` exits 0 on this branch's tree again.

Those eleven are **not** covered by any lane, and this record says so rather
than implying otherwise: indentation and import ordering are formatter rules
with no cheap SDK-independent check, ADR-0134's trade, caught by CI on `main`
after the merge — and three of the eleven arrived on `main` in the hours
between the bead being written and this branch being fixed, which is the cost
of that gap stated in one line of drift. The bead's alternative — put the
scoped format step back on the hand-off path — is rejected in §Alternatives; it
costs ~45 s per changed project on every one of hundreds of merges a day to
catch a handful of lines.

## Alternatives

- **Put the scoped format step back on the hand-off path** (`eng/verify.sh
  --format` before every hand-off, or `dotnet format` inside the fast lane).
  It catches every class rather than one, and it is what ADR-0134 removed with
  the maintainer's explicit instruction (SpatialEngine-6g7): "as fast as
  possible, some safety traded away, formatting run once in a while rather than
  on every merge, and a few minutes at most for the verify and the merge
  together." ~45 s per project per merge is minutes a day for a rule CI already
  enforces once per push.
- **Grow `tools/trailing_whitespace.py` to read `insert_final_newline` too.**
  Rejected: it contradicts ADR-0143 §2 and its own acceptance criteria, and one
  rule per script is what keeps the ~2 s cost and the "no growth" claim honest.
- **A Roslyn analyzer for `FINALNEWLINE`, enforced in the build.** The better
  shape if the rule ever grows, and rejected for the measured reason ADR-0143
  gives: a build-time analyzer costs solution-wide work every lane pays, while
  a two-second `grep`-shaped check belongs on every lane and needs no SDK.
- **Read all of `[*]`** rather than the formatter's file set. Rejected here
  because it lands 190 findings in one commit and needs exemptions for captured
  fixtures; filed as its own bead rather than smuggled in.
- **Leave it to CI on `main`.** That is the status quo, and it is what produced
  a red formatter on `main`: the detector is on the far side of the merge, so
  the merge it was supposed to protect is green.

## Not decided

Whether the `[*]` final-newline rule outside C# is enforced, and with which
exemptions for captured `psql` output, generated snapshots and ADR prose — the
separate bead, whose answer decides whether `tools/final_newline.py` grows a
file-type table or the fixtures are byte-stable and exempt instead.

Whether the indentation and import-ordering rules ever get a check of their own.
Nothing cheap and SDK-independent reads them, and a second formatter is what
ADR-0143 refused.

## Consequences

- A C# file with no final newline fails the fast lane and CI, instead of
  reaching `main` and turning the post-merge CI run red.
- `main`'s formatter is green again: `dotnet format SpatialEngine.slnx
  --verify-no-changes` exits 0 on this branch's tree.
- Every lane pays ~2 s for the check over the repository and under 0.1 s for
  the change set; a scoped lane pays it for the changed files only.
- 20 C# files and 8 other formatting violations changed in this branch. The
  former is a byte each; the latter is the formatter's own output.
- The `[*]` scope of the rule stays unenforced for everything that is not C#
  (see §2), and indentation and import ordering stay the formatter's, on CI
  after the merge (see §4).

## References

- `tools/final_newline.py`, `tools/test_final_newline.py`, `eng/verify.sh`
  (`final_newline_step`, called by all three lanes), `.github/workflows/ci.yml`,
  `.editorconfig`
- ADR-0134 (the formatter leaves the merge path, and the trade this record
  closes part of), ADR-0143 (the sibling check, and the one-rule clause this
  record routes around), ADR-0146 (the shape a later repository rule took),
  ADR-0109 (what the formatter costs), ADR-0118 as amended by ADR-0134 (the
  lanes)
- `AGENTS.md` ("Commands"), `eng/swarm-runbook.md`
- SpatialEngine-744 (this record's bead), SpatialEngine-3rz (the `[*]` scope
  outside C#, filed from it)

## Measurements

Taken 2026-10-01 in worktree `1mmcart7`, SDK 10.0.400.

| Question | Measurement |
| --- | --- |
| What does a clean `origin/main` fail the formatter with? | `dotnet format SpatialEngine.slnx --verify-no-changes --no-restore` at `eb909b5c`: exit 2, 22 violations in 16 files — 14 x FINALNEWLINE, 7 x WHITESPACE in `src/Spatial.Contracts/Http/FeatureQueryWire.cs`, 1 x WHITESPACE in `tests/integration/Spatial.Host.Tests/AuthEndpointTests.cs`, 1 x IMPORTS in `tests/unit/Spatial.Operations.NetTopologySuite.Tests/NtsGeometryRelationsFidelityTests.cs`. |
| How many more does this branch's own base carry? | 11 x WHITESPACE in three further files — `GridShiftCandidate.cs`, `TestFixtures.cs`, `PublishedGridDeploymentTests.cs` — none of them in the bead's list, because they landed on `main` after `eb909b5c`. Same run, same clean tree, after the twenty fixes above had been made. |
| Does the formatter read a `.csproj`? | No. `dotnet format clients/dotnet/Spatial.Client/Spatial.Client.csproj --verify-no-changes --no-restore` on a project file with no final newline: exit 0, no output. |
| How many C# files lack a final newline on this branch's base? | 14 at `eb909b5c`, 20 at this branch's HEAD (six more landed since, from SpatialEngine-0178 and its neighbours). |
| How many files git ships lack one, over all of `[*]`? | About 190, across ADR prose, `research/compat` and `tests/fixtures` captures, `.csproj`/`.json`/`.ts`/`.md` and `.editorconfig` itself. Measured with `tools/final_newline.py` before its file-type table was added. |
| What does the check cost? | Same shape as `tools/trailing_whitespace.py`, which ADR-0143 measured at 1.9 s clean over `git ls-files --cached --others --exclude-standard` and under 0.1 s over a 6-file change set. |