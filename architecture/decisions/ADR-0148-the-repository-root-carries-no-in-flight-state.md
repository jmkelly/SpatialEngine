---
status: accepted
date: 2026-10-01
deciders: maintainer + agent
summary: The repository root carries no document that answers "what is happening now" and exactly one changelog, at `docs/CHANGELOG.md` — `HANDOFF.md` (142 lines, last touched 2026-09-15, opening with a claim about the active work that `bd ready` already contradicted) is deleted and the changelog moves out of the root, and **every lane of `eng/verify.sh` and the CI `verify` job call `tools/doc_surface.py`**, which fails on a root document whose job is in-flight state, on a second changelog, and on a released heading above `<Version>`. The check is called directly rather than left to the `tools/**`-only tooling suite, because reintroducing one is a docs or `src` change (ADR-0143, ADR-0146). Generating the `## [Unreleased]` section is deferred, not dismissed: 107 merges have landed since `v0.3.0`, so a merge-scoped generated section is a `--write` on every merge, and the bead queue it would read is not in a CI checkout at all.
amends: ADR-0118, ADR-0134
---

# ADR-0148: The repository root carries no in-flight state, and the changelog is a release artefact

## Context

Two documents sat at the root of this repository for a month and a half, and
both answer questions that a different, better source already answers.

`HANDOFF.md` was 142 lines, last touched 2026-09-15, and written for "the next
agent taking over". It opened by naming the active work — "the GeoServices REST
track is the active work" — which `bd ready` had already contradicted. It
carried a repository-state section (test counts, branches), a per-track status
section, and a list of hard-won gotchas. In-flight state is the bead queue's:
`bd` is versioned (Dolt), queryable, and where every workflow in `AGENTS.md`
already starts. With `eng/swarm-runbook.md` beside it, the root and the queue
were **three** sources of truth for what is happening now, and duplication of
one meaning in several places is the cost to remove (writing-for-agents,
*Pruning*). A stale document that answers "what is happening now" is worse than
no document, because it is confident.

`CHANGELOG.md` was 1384 lines at the root, of which 1038 were the
`## [Unreleased]` section: 79 commits touched it in the 17 days since `v0.3.0`,
hand-merged from merge to merge by a rule no gate touched. An agent making a
change never needs 843 lines of release history; it needs `git log` over the
path it is touching. Only the release checklist (`RELEASING.md`) needs the whole
file, and it runs a few times a year.

The corpus around them is good and this record is not a proposal to prune it.
`architecture/distilled/README.md` is the correct index-plus-just-in-time
pattern and `AGENTS.md` is 4.8KB. The failure is not writing; it is that
nothing holds a root in shape once the two files are gone.

## Decision

**The root carries no document that answers "what is happening now", the
changelog is a release artefact at `docs/CHANGELOG.md`, and every lane reads
that through `tools/doc_surface.py`.**

### 1. The two moves

`HANDOFF.md` is deleted, and what in it was not already a bead or a code
comment became one. Most of it was already durably recorded somewhere else,
which is why deleting it lost no knowledge: the raster gotchas are ADR-0044 and
`architecture/distilled/host-and-clients.md`, the MapLibre worker pin is a
comment in `MapScreen.tsx`, the middleware ordering is visible in
`Program.cs`, and the one CRAP finding the file excused is waived in
`quality-waivers.json` with its evidence. The residual — the gotchas with no
durable home — is a bead.

`CHANGELOG.md` moves to `docs/CHANGELOG.md`, and every path that named it is
updated: `RELEASING.md` (which is the release checklist and so the authority on
the path), `README.md`, the `<Version>` comment in `Directory.Build.props`, and
the exclusion in `Spatial.Architecture.Tests`' naming sweep. The ADR-0146 and
ADR-0141 records that mention a `CHANGELOG.md` merge are history and keep the
root path: they are describing a commit, not a file to open.

### 2. The check, and why it is not a test

Deleting the two files is the cheap half and it does not hold: nothing stopped
them landing, so `tools/doc_surface.py` is what holds. It is a check, not a
test — it reads three things, reports `path: what is wrong`, exits 1, changes
nothing, has no `--fix` and needs no .NET SDK — and every lane calls it
directly, beside `tools/trailing_whitespace.py` (ADR-0143) and
`tools/conflict_markers.py` (ADR-0146).

It is called directly rather than left to the `tools/test_*.py` suite because
that suite runs only when the change set touches `tools/**`
(`run_python_tooling`, `tools/verify_scope.py`), and reintroducing a root
session note is a **docs or `src` change** — the same shape as the merge ADR-0146
records, where the fast gate read nothing that could see the defect and CI on
`main` caught it a week later. The CI `verify` job spells its steps out rather
than calling the script, so it carries the step too, for the same reason.

It is a repository check rather than a check on the change, so unlike the
whitespace step it has no scoped variant and no unreadable-change-set
fallback: there is nothing to narrow.

### 3. The three rules, and what each one is against

| Rule | The defect it names |
| --- | --- |
| No document at the root whose job is in-flight state | the `HANDOFF.md` shape: a file that answers "what is happening now" and is therefore stale the moment a bead closes |
| Exactly one changelog, at `docs/CHANGELOG.md` | a second `CHANGELOG.md` beside the one `RELEASING.md` names — two answers to "what shipped", and the release moves one of them |
| `<Version>` and the changelog agree | `RELEASING.md` steps 2 and 3 are one edit, and a `## [x.y.z]` heading above the product version is a release whose version was never bumped |

The first rule is a **list of names, not a shape**. A shape — "a `.md` file at
the root is a finding" — would be a rule about every file an agent adds, and
`AGENTS.md`, `README.md`, `RELEASING.md` and the generated `arch-index.md` are
all at the root and none of them goes stale between sessions. Names are matched
on the stem, case-insensitively, so `HANDOFF`, `handoff.md` and `Handoff.MD`
are one name rather than three. The rule is about the root only: a task's notes
under `research/` is a document with a pointer at it, which is the shape the
corpus is supposed to have.

The third rule compares versions as numbers, because `0.10.0` is above `0.9.0`
and a string comparison says otherwise, and it reads `## [Unreleased]` as not a
release. A `<Version>` it cannot parse is **reported** rather than skipped,
because silence would make every comparison pass.

### 4. Where it runs

Every lane, after the conflict-marker check and before the ADR doc gate: both
are repository checks that precede the expensive part of a lane, and this one is
cheaper than either (measured: 15 ms for the read, 155 ms including interpreter start, against 1.2 s
and ~2 s).

| Lane | What it runs |
| --- | --- |
| `--fast` (the merge gate) | the check, unconditionally — **not** inside the `tools/**`-only tooling gate |
| `--format` | the check |
| `--full` | the check, and the tooling suite as before |
| CI `verify` | the check as its own step, plus the tooling suite |

`tools/test_doc_surface.py` keeps the unit tests over the three rules and the
tests that are about the gate rather than the checker: every lane runs it, the
helper runs the script, the call stands **before** the `RUN_TOOLING` conditional
in the default lane, the CI job carries it, and the repository passes the check
as a command.

### 5. Generating the `## [Unreleased]` section is deferred, not dismissed

A doc audit on this bead argued that relocation is the wrong half: relocating
843 lines out of the root does not remove the thing that rots them, because the
entries are hand-merged into a file no gate touches, and the durable inputs
already exist — the bead title and notes, the ADR ids, and the `Task: <id>`
commit trailer. The argument is right about the rot and wrong about the
mechanism available *today*, for two measured reasons.

**A merge-scoped generated section is not a per-merge gate.** The only inputs
that name what shipped are the merge commits and the beads, and both change
when the merge lands. 107 merges have landed since `v0.3.0`, so a section
generated from `v0.3.0..HEAD` is a `--write` — and a follow-up commit, and a
lane that is red in the meantime — on essentially every merge. That is the cost
ADR-0134 removed from the merge path on purpose, and re-introducing it for a
documentation file is a poor trade. The same argument rules out gating a
hand-edit: a check that fails whenever the committed text differs from the
generated text is a check that fails on every merge.

**The bead queue is not in a checkout.** `.beads/` is gitignored and lives in
the git common dir (`AGENTS.md`, *Task queue*), so it is present in every
worktree on this host and absent from every CI clone. A section generated from
bead metadata is a rule that runs in the developers' worktrees and silently
does not run in the job that follows every merge — the false green ADR-0143 and
ADR-0146 are about, in the direction the other way round.

So this record fixes what can be held (the root, the path, the version) and
leaves generation to a bead that has to answer the input question first. Until
then the `## [Unreleased]` section stays hand-merged, and the header of
`docs/CHANGELOG.md` says so and says what a reader wants instead: `git log`
over the path, and the bead.

## Consequences

- The root of the repository is **two files** shorter: a 142-line stale handoff
  and a 1384-line release artefact, of which 1038 were a hand-merged section
  that no gate read.
- An agent's file listing no longer carries a thousand lines of history past it,
  and `AGENTS.md` names `docs/CHANGELOG.md` as a release artefact and `bd` as
  the in-flight state, with `git log -- <path>` as what a change actually wants.
- The merge gate pays **15 ms** for the check, and reads three rules it did not
  read before, on every merge.
- The check cannot see knowledge *lost* by a deletion; the durable home for
  what `HANDOFF.md` held and nothing else held is a bead, and a `bd close` that
  removed a file without one would be the hole in this record.
- The `## [Unreleased]` section is still hand-merged. This record does not
  pretend otherwise, and the follow-up bead is what makes it generated or makes
  the case that it should not be.
- Naming a root document is a list, so a new session-note name (`HANDOFFS.md`,
  `INBOX.md`) is not a finding until somebody adds it. That is a review cost of
  one line rather than a rule about every file an agent adds.

## References

- `HANDOFF.md` at `9d01add` (last content change, 2026-09-15), deleted here.
- `docs/CHANGELOG.md`: 1384 lines at the root on 2026-10-01, of which 1038 were
  `## [Unreleased]`; 79 commits touched it in the 17 days since `v0.3.0`, and
  107 merges landed in that span.
- ADR-0118 (the lanes), as amended by ADR-0134 (the fast lane is the merge
  gate); ADR-0141 (a generated, gated documentation artefact);
  ADR-0143 and ADR-0146 (a rule that runs only when `tools/**` changed is not
  a gate).
- writing-for-agents, *Pruning*: keep each meaning in a single source of truth;
  a document that goes stale between sessions is sediment.
- SpatialEngine-imz.3 (G6) and its parent's doc audit; the follow-up bead for
  the generated `## [Unreleased]` section.

## Measurements

| Question | Answer |
| --- | --- |
| What did the root carry that will not come back? | `HANDOFF.md` (142 lines, 2026-09-15) and `CHANGELOG.md` (1384 lines, 1038 of them `## [Unreleased]`) |
| How many merges land between releases? | 107 since `v0.3.0`, 203 in the repository's history |
| What would a merge-scoped generated `## [Unreleased]` cost per merge? | one `--write` and one commit per merge, and a red lane in between |
| Is the bead queue readable from a CI clone? | no: `.beads/` is gitignored and lives in the git common dir |
| What does the check cost? | 15 ms for the read over the root, `docs/CHANGELOG.md` and `Directory.Build.props`; 155 ms as a command including interpreter start; no .NET SDK |
| What happens to the lost knowledge? | it goes to the beads and the code comments that already held most of it; the residual is a bead |
