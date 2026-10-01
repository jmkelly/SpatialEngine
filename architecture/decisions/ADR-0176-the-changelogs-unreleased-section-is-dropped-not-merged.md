---
status: accepted
date: 2026-10-04
deciders: maintainer + agent
summary: When the generated-changelog branch (SpatialEngine-v47, ADR-0173) rebases, main's hand-merged `## [Unreleased]` section is **dropped, not carried forward** — the resolution takes the branch's `docs/CHANGELOG.md` whole and regenerates the three index files with `tools/arch-index.py --write`, because a resolution that preserved main's bullets fails the branch's own `tools/changelog.py --check`, and because the prose a human hand-wrote for past merges is rendered back from the commits at the next release.
related: ADR-0173
---

# ADR-0176: The changelog's Unreleased section is dropped, not merged

## Context

ADR-0173 deleted `docs/CHANGELOG.md`'s `## [Unreleased]` section and replaced
release-time generation with `tools/changelog.py`, gated by a `--check` that
fails on a hand-merged `## [Unreleased]` heading. That made the change
unmergeable for four coordinator ticks: `bd/SpatialEngine-v47` could not rebase
onto `main`, because `docs/CHANGELOG.md` conflicted and either resolution was an
editorial call nobody had authorised — take the branch, and 1038 lines of
hand-written release narrative are deleted from a file the repository still
reads; keep `main`'s, and the branch's own gate is red on the result.

The tick stopped rather than choose, escalated it (SpatialEngine-u2x.63), and
held the swarm's backlog gate while 18 ready beads sat idle. The escalation
asked for a disposition. It was framed as an open choice between ratifying the
drop and amending ADR-0173 to seed the generated section from the existing
`Unreleased` body. It was not open, and the bead said why.

**Carrying `main`'s entries into the branch is impossible as stated.** Run the
branch's tool against `main`'s tree and it fails, by design:

```
$ python3 tools/changelog.py --root /home/james/Work/SpatialEngine --check
EXIT=1
docs/CHANGELOG.md:18: an ## [Unreleased] section is hand-merged from merge to
  merge by a rule no gate touched - 1038 lines of it, hand-edited across 107
  merges
```

ADR-0173 states the same outcome as its design rather than as a casualty:
"There is no `## [Unreleased]` section, and there is a gate that says so",
between releases `git log v<previous>..HEAD` answers what shipped, and the
release-time section is generated once, by `RELEASING.md` step 3.

So the section goes. The question left for a human was whether the *narrative*
survives the transition, and ADR-0173 already answers it: it does not survive
as prose, because it is re-rendered from the commits that produced it, and
every entry's title, ids and narrative are already in those commits.

## Decision

**Take the branch's `docs/CHANGELOG.md` whole on the merge, and record that the
hand-written `Unreleased` narrative is not carried forward verbatim.**

The resolution is mechanical and is the same one every future merge that
touches `ADR-0173`'s files gets:

1. `docs/CHANGELOG.md` — take the generated-changelog side. A textual merge
   that keeps any `## [Unreleased]` heading is wrong by construction: it fails
   `tools/changelog.py --check`, which every lane and the CI `verify` job run.
2. `arch-index.md`, `architecture/decisions/README.md` and
   `architecture/distilled/README.md` are `tools/arch-index.py --write`
   output. Regenerate them; never hand-merge them (ADR-0141).
3. Confirm the disposition rather than a merge accident: `python3
   tools/changelog.py --check` and `python3 tools/arch-index.py --check` both
   pass on the merged tree.

The cost is accepted deliberately: the sentence a human wrote for a past merge
is not carried forward verbatim. It is replaced by the title, ids and commit
body the history already holds, rendered at the next release.

## Alternatives

**Carry `main`'s `Unreleased` bullets into the branch.** Rejected as
impossible, not merely unwise: the branch's own gate fails on the result, so
every lane is red until the section is removed again. A merge that must be
followed by an un-gated `--write` is the cost ADR-0173 was written to delete.

**Amend ADR-0173 to seed the generated section from the existing `Unreleased`
body**, so the release narrative survives the transition verbatim. Rejected for
this merge, and worth saying why: the prose is the thing that rotted — 79
commits touched it in 17 days, hand-edited across 107 merges — and a migration
path for hand-written text re-admits the rot into the artefact. The material
itself is not lost; it is in the commits, which is where the generator reads it.
A migration would need a record of its own, and the next release would still
render from git.

**Hand-merge the three index files along with the changelog.** Rejected: they
are generated, so a hand-merge is a second copy of the ADR-0173 rot in a file
that already has a `--write` and a staleness check (ADR-0141).

## Not decided

Whether a release entry should carry more than the commits do. ADR-0173 §"Not
decided" keeps this open, and this record does not change it: a release that
wants the long form pastes it into the frozen released section by hand. This
disposition makes the question sharper, not smaller — after the next release
the prose past merges exists in exactly two places, the commits and the
released section, and there is no third hand-merged one to drift from them.

## Consequences

- **SpatialEngine-v47 is unblocked**, and with it the swarm's backlog gate: the
  branch rebases and hands off, and 18 ready beads can drain.
- **A hand-written release narrative for a past merge is not preserved
  verbatim.** Anyone who wants a merge's prose to read as it did reads
  `git log -- <the bead's commits>`, or the released section after the next
  release renders it.
- **Every future merge touching these four files gets the same three-step
  resolution**, and it is written down here rather than left to the next agent
  to escalate. The escalation cost four ticks and the idle swarm behind it.
- **A textual merge of `docs/CHANGELOG.md` is no longer available as a
  resolution.** That is the point: the file's prose between releases is
  whatever the released sections say, and nothing else.

## References

- ADR-0173 (the generated changelog and the gate this conflict comes from),
  ADR-0148 (the relocation and the deferred generation it closed),
  ADR-0141 (the generated register, regenerated rather than hand-merged),
  ADR-0152 (the `Task:` trailer the generator's input is built from),
  ADR-0134 (why the merge path pays for no `--write`).
- `docs/CHANGELOG.md`, `tools/changelog.py`, `tools/arch-index.py`,
  `RELEASING.md` step 3, `eng/verify.sh`, `.github/workflows/ci.yml`.
- SpatialEngine-v47 (the branch this unblocks), SpatialEngine-u2x.63 (this
  bead, the escalation), coordinator tick 2026-10-01T22:39 (the abort).

## Measurements

| Question | Answer |
| --- | --- |
| Can `main`'s `Unreleased` section be carried into the branch? | no — `tools/changelog.py --check` against `main`'s tree exits 1 with one finding, the 1038-line hand-merged section (2026-10-01) |
| How many conflicting files? | four: `docs/CHANGELOG.md` (the decision) plus `arch-index.md`, `architecture/decisions/README.md`, `architecture/distilled/README.md` (all `tools/arch-index.py --write` output) |
| How much hand-editing did the section absorb? | 79 commits touched it in the 17 days after `v0.3.0`, across 107 merges |
| What replaces the dropped prose? | `python3 tools/changelog.py --range v0.3.0..HEAD --date <date> --write` at the next release: 192 entries from 107 merges, one per bead (ADR-0173's own measurement) |
| What does it cost to resolve? | one `tools/arch-index.py --write` and two `--check`s; no editorial work |