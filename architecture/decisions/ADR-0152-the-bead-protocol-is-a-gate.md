---
status: accepted
date: 2026-10-01
deciders: maintainer + agent
summary: The **bead protocol is a gate, not prose**: `tools/beads_gate.py` judges, on every lane of `eng/verify.sh`, that a closed bead's merge commit carries a `Task: <id>` trailer naming that same bead, and that a commit touching `src/Spatial.Contracts/**` or `src/Spatial.Core/**` either changes a decision record or cites `ADR-NNNN` in its body. The ADR citation and register checks are G1/G2's and are read through `tools/arch-index.py` rather than implemented twice. The trailer check binds from the merge commit that carried this gate onto `main` — history written before the trailer existed is grandfathered and counted, not failed — and the one input CI cannot have, the bead queue, is reported as not judged rather than passed (SpatialEngine-imz.4).
amends: ADR-0118
related: ADR-0141, ADR-0146, ADR-0150
---

# ADR-0152: The bead protocol is a gate, not prose

## Context

The geometry and contract walls in this repository are enforced in code:
`Spatial.Architecture.Tests` fails a `Spatial.Core` that depends on anything, and
a contract that grows a third-party type. That half of "the hard walls" is
deterministic. The other half is prose — in `AGENTS.md`, in
`eng/swarm-runbook.md`, and in the merge tool's own docstring — and it has
already failed in-tree, four separate ways, all of them in git rather than
hypothetical:

* **The mis-dispatch.** A coordinator's prompt named bead `.2` in the body and
  `.4` in the header. Three workers each read the body and built bead `.2`
  inside the `.1`, `.3` and `.4` worktrees. The runbook grew a substitution rule
  to prevent it, which is a prose fix to a failure prose caused.
* **The reclaim race.** `bd reclaim` keys on lease age alone, and a
  long-running worker does not heartbeat, so "expired" only means "has not
  heartbeated lately". One tick released eight leases whose agents `paseo` still
  reported as running, dropping live work back into `bd ready` for a
  double-claim. `tools/bd-safe-reclaim.py` is the fix, and it is a wrapper an
  agent has to *remember* to call.
* **The context-free merges.** `Merge SpatialEngine-u2x.9` and `.u2x.18` landed
  with conflict resolutions written by a coordinator that did not hold the
  workers' context.
* **The rescue commits.** Three commits exist only to recover work the reclaim
  race orphaned (`WIP: preserve uncommitted work ... swarm reclaimed stale
  lease`).

The common shape is that each of these was *possible* while every instruction
was technically satisfied. Nothing read them back.

Two further facts shape the answer. The corpus was written under no rule, so a
rule that binds from its own start must grandfather what came before — the
precedent is ADR-0150's shape rule, which binds from its own number forward for
exactly this reason. And the protocol's checks are all *mechanical*: a trailer is
present or absent, a bead id matches or it does not, a diff names a wall
directory or it does not. None of them needs judgement, which is what makes them
gates rather than audits.

## Decision

**Judge the two mechanical halves of the bead protocol on every lane, and have
the merge tool write the trailer itself.**

`tools/beads_gate.py` is the check, called by every lane of `eng/verify.sh`
beside the doc gate, for the same reason ADR-0143 and ADR-0146 give the other
repository checks: a `tools/test_*.py` runs only when the change set touches
`tools/**`, and both of these failures arrive on a `src` merge.

1. **The trailer.** A merge commit whose subject is `Merge <bead-id>: …` must
   carry `Task: <bead-id>`, and the ids its `Task:` trailers name must include
   the bead it merges. A merge of a bead the queue has no record of is a finding
   too. A bead that is *open* is not judged: the merge is published before the
   close, and a gate that failed the gap between them would fail every merge in
   flight.

2. **The wall.** A commit touching `src/Spatial.Contracts/**` or
   `src/Spatial.Core/**` must change a file under `architecture/decisions/`, or
   cite `ADR-NNNN` in its body. This is `AGENTS.md`'s "behaviour changes land
   contract, SDK, test and ADR updates together" as a comparison rather than a
   review. Only those two directories are walls; a host change with no record is
   ordinary work.

Checks 3 and 4 of the bead — every `ADR-NNNN` cited anywhere resolves, and the
register is complete — are G1/G2's, implemented once in `tools/arch-index.py`,
and are **read through it**, never reimplemented: `beads_gate.py` reports that
delegation on every run and `--adr` promotes the shared reads to findings for a
standalone run.

Three boundaries, each deliberate:

* **The trailer binds from the merge commit that carried this gate onto `main`.**
  The shape rule's precedent. Merge commits before it are grandfathered, and the
  count is printed on every run, because a scope that is invisible is
  indistinguishable from a check that passed.
* **The merge tool writes the trailer.** `tools/bd-merge-bead.py` composes the
  merge message, so a protocol that only an agent remembers to follow is exactly
  the protocol that was already broken.
* **An absent queue is reported, not passed.** The bead queue is local
  coordination state in the shared git dir; CI has none. A run that cannot read
  it prints that check 1 was *not judged* and judges the rest. `--strict` turns
  that into a finding for a caller that is on a machine where the queue must be
  there.

## Alternatives

**A `pre-commit` git hook, alone.** Closer to the failure — it refuses the
commit rather than the merge — but it is per-clone state in the shared git dir,
it is skipped under `--no-verify`, and it does not see the merge commit, which is
the commit that carries the bead. It is a complement to the lane check, not a
substitute for it, and it is deliberately not this record's mechanism: changing
`core.hooksPath` for eight concurrent worktrees to get it is a larger blast
radius than the failure it removes.

**Rewriting history to add the trailers.** Would make the rule bind from the
beginning, at the cost of rewriting every published sha on `main` and every
closed bead's recorded commit — which `tools/bd-merge-bead.py --audit` reads by
sha to find stranded work. Trading a verifiable audit trail for a tidier
backlog is the wrong direction.

**Judging the whole of history, ADR wall included.** The wall rule is 30 months
of merges' worth of "we changed a contract and cited nothing"; failing all of it
is a red lane nobody runs, and it cannot be cleared without the same rewrite.

**Judgement checks** — "does this commit look like it changed behaviour", "is
this ADR relevant to this diff". These are the audits `eng/quality-audit.sh`
already reports, and they are not gates: they need a reader, and a gate that
waits for a reader is a queue.

## Not decided

**Whether `bd-safe-reclaim.py` becomes a hook.** `bd` exposes no registration
surface for reclaim events — `bd config set hooks.*` is rejected by name, and
the only chaining `bd hooks install` offers is the content outside the managed
markers in a *git* hook file, which no reclaim path runs. Until there is a
surface, the deterministic half of the reclaim policy is what this record and
`tools/bd-safe-reclaim.py` already give: a closed bead whose work never reached
`origin/main` is a finding (`bd-merge-bead.py --audit`), and a lease that a live
agent still holds is never reaped. The evidence for the missing surface is
recorded on SpatialEngine-8oe, which carries it.

**Whether the grandfathered window ever closes.** If the merge tool stops writing
the trailer for some path — a hand-run `git merge`, a conflict resolved by a
human — the count grows. A second record would close the window by declaring an
end date rather than letting it widen.

## Consequences

The mis-dispatch becomes visible at the merge rather than three worktrees later:
the commit's bead and its `Task:` trailer disagree, and the lane says so. A
contract change lands with its decision record or cites the record it amends, and
the citation is itself checked by `arch-index.py --check`, so a wall change
citing an ADR that does not exist fails twice.

The costs are real. The gate is repository-wide rather than scoped, because the
trailer is a property of how merges are written and the wall is a property of
what a commit says it did, neither of which narrows on a scoped lane; it costs a
`git log` walk plus one `git show` per commit in the change set and a merge walk
of the whole history. `bd-merge-bead.py` must keep composing the trailer, and a
hand-run `git merge --no-ff` on `main` is now a red lane — which is the point,
and is why the grandfathered count is printed rather than hidden. Check 1 cannot
run where the queue cannot be read, so on CI the trailer rule is enforced by the
merge tool and by the local lanes, not by the CI step.

## References

* `tools/beads_gate.py`, `tools/test_beads_gate.py` — the gate and its tests.
* `tools/bd-merge-bead.py` — `merge_message()`, which writes the trailer.
* `eng/verify.sh` — `beads_gate_step()`, called by the format, full and fast
  lanes; `.github/workflows/ci.yml` — the same check, with `--no-queue`.
* `tools/arch-index.py` — checks 3 and 4, read through `shared_adr_findings()`.
* `tools/bd-safe-reclaim.py` (SpatialEngine-u2x.30, SpatialEngine-k0p) — the
  reclaim wrapper, and the reason the lease half is not a gate.
* ADR-0118, as amended by ADR-0134 — the lanes this check joins.
* ADR-0141 — the generated register and index whose checks are shared here.
* ADR-0150 — the grandfathering precedent, and the closed-section shape this
  record is written in.
* SpatialEngine-imz.4 — the bead, and the four in-tree failures it names.
