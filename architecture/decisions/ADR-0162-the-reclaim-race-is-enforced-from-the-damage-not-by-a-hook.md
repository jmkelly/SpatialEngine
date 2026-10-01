---
status: accepted
date: 2026-10-02
deciders: maintainer + agent
summary: The bead-protocol gate gains a **lease check**: on every lane, `tools/beads_gate.py` reports an open bead that holds no lease while `paseo` still reports an agent *running* in that bead's own worktree — the shape of the reclaim race, read from the damage because `bd` exposes no registration surface for a reclaim hook. The liveness question is asked through `tools/bd-safe-reclaim.py`'s own `protect_reason`, so the gate and the documented fix cannot drift; an *idle* session parked on an unleased bead is printed rather than failed, and a lane that cannot read `paseo` says so instead of passing (SpatialEngine-8oe).
amends: ADR-0152
related: ADR-0118, ADR-0141
---

# ADR-0162: The reclaim race is enforced from the damage, not by a hook

## Context

ADR-0152 made the bead protocol a gate. One of the four failures it names is
the reclaim race, and ADR-0152 left it where it was — prose with a wrapper
beside it:

> `tools/bd-safe-reclaim.py` is the fix, and it is a wrapper an agent has to
> *remember* to call.

The bead asked for the wrapper to be **wired as a `bd` hook**, so the policy was
enforced rather than advised. There is no surface to wire it to, and the evidence
is in the bead:

> - `bd config set hooks.pre-commit '<cmd>'` is refused by name: Warning:
>   "hooks.pre-commit" is not a recognized config key. Use 'custom.*' for
>   user-defined keys. A `hooks:` block written into `.beads/config.yaml` is
>   ignored — `bd hooks run` exits 0 and runs nothing.
> - `bd hooks install --help` offers one chaining mechanism: content outside the
>   `--- BEGIN/END BEADS INTEGRATION ---` markers in an installed *git* hook file
>   ("existing hook content always runs alongside the bd section"). No git hook
>   runs on a coordinator reclaim step.
> - The binary does carry `internal/storage.(*HookFiringStore).
>   ReclaimExpiredLeases`, so hooks fire around a reclaim — but nothing in
>   `bd config --help`, `bd hooks --help` or `bd prime` names how to register
>   one.

So the surface is upstream's to add, and the cost of an upstream ask is that the
protection stays absent until it lands. The failure it protects against is not
hypothetical and it is not small: one tick released eight leases whose agents
were all still running (SpatialEngine-u2x.30), and three commits in this
repository exist **purely** to rescue work the race orphaned — including the
`WIP: preserve uncommitted work ... swarm reclaimed stale lease` commit on the
branch this record was written on.

What is detectable is the *aftermath*, and it is mechanical. A reclaim takes the
assignee and the lease expiry back and drops the bead into `bd ready`; a live
agent does not stop. So "no lease, and `paseo` still has somebody mid-turn in
that bead's own worktree" is exactly the pair the race produces, and the two
inputs to ask it are the two the runbook already produces. That is the shape of
this decision: enforce the policy from the damage, because the damage is what
is readable.

## Decision

**Judge the reclaim race on every lane, from the pair the race produces: an
open bead with no lease while a running agent is still in its worktree.**

`tools/beads_gate.py` grows this as a fifth check, beside the trailer and the
wall ADR-0152 established and before the two ADR checks it reads through
`arch-index.py`. It is read from the two inputs the runbook already names, and
each is a boundary rather than a convenience:

- **The queue** is local coordination state in the shared git dir. A run that
  cannot read it reports check 1 as *not judged* — and now check 3 likewise —
  and judges the rest. `--no-queue` and `--no-paseo` skip a read deliberately;
  `--strict` turns an unreadable input into a finding rather than a pass.
- **The liveness question has one answer.** "Is the agent holding this bead
  alive" is asked through `tools/bd-safe-reclaim.py`'s own `protect_reason`,
  loaded from beside the gate rather than reimplemented. The wrapper already
  matches both ways the runbook records a hold — the `bd/<id>` worktree resolved
  through `paseo workspace ls` against every live agent's cwd, and the `agent
  <id>` the claim recorded in the bead's notes. A second implementation in the
  gate would be a second answer to one question, and the reason checks 4 and 5
  are read through `arch-index.py` rather than written again is precisely that
  the second answer is the one nobody runs.

Two boundaries inside the check, both of which are what kept it mergeable:

- **`running` fails, `idle` is reported.** A session parked between turns on a
  bead whose work is finished or superseded is a coordinator's business, not a
  lane's: on the day this landed, two such holds existed in this repository
  (`SpatialEngine-xr4`, `SpatialEngine-imz.9`) and a gate that failed them would
  have been a gate nobody could merge from. They are printed on every run —
  which is how those two were found — and counted in the run's scope line.
- **A bead nobody ever claimed is not a finding.** An open bead with no lease
  and no agent anywhere near it is what `bd reclaim` is *for*. The finding
  needs both halves.

The finding names the recovery, because a check that reports a shape without
saying what to do about it is a survey: it points at `tools/bd-safe-reclaim.py`,
warns off bare `bd reclaim`, and says to read the bead's worktree before
touching it.

## Alternatives

- **Ask `bd` upstream for the registration surface** — expose reclaim and lease
  events to `bd hooks`. This is the bead's first option and it is the right
  long-term one; the internal `HookFiringStore.ReclaimExpiredLeases` already
  exists, so only the configuration half is missing. It loses here because it
  delivers nothing this year: the wrapper stays remembered rather than enforced
  until an upstream release lands, and the three rescue commits are still
  avoidable meanwhile.
- **A git hook**, the one chaining mechanism `bd hooks install` names. No git
  hook runs on a coordinator reclaim step, so it would fire on commits rather
  than on the reclaim — a different event wearing the same name.
- **Fail the idle case too.** Stricter, and red on the day it landed, for two
  holds whose work was already finished. A gate that fails the state this
  repository is actually in is a gate that gets disabled.
- **Make the coordinator call the wrapper** (a tick change rather than a lane
  change). Better placement — it is the thing that reclaims — but the
  coordinator is not in this repository, so the policy would again be enforced
  by something outside it, which is the failure mode this record exists to end.
- **Reclaim proactively on every lane** rather than reporting. A lane that
  mutates in-flight state is a lane that can destroy it, and the safe outcome
  when the inputs disagree is to leave state alone and let a human look.

## Not decided

- **Whether the check should bind the coordinator's own writes.** It reads what
  is there; it does not stop a reclaim, and nothing in `bd` lets this
  repository stop one. If `bd` gains the registration surface, this record's
  first option lands and this check becomes the backstop rather than the
  enforcement.
- **What a human is meant to do with the idle holds it prints.** They are the
  signal that a session outlived its bead; whether the coordinator should reap
  them, close them or ignore them is a queue policy question, and this record
  only makes them visible.
- **Whether `paseo` should report a lease.** The two inputs are read here
  because they are the two that exist. A `bd` that carried the holder's
  liveness in the bead row would make this a one-input read and would remove the
  notes-id half of the match.

## Consequences

- The race is caught on the lane that would otherwise carry the orphaned work,
  rather than by whoever notices a rescue commit. The three commits that exist
  only to preserve uncommitted work have a check that would have failed the lane
  before the work was lost.
- `tools/bd-safe-reclaim.py` is now a **library** as well as a command: the gate
  imports it by path, so the two halves of the lease policy cannot drift, and a
  change to the hold's definition breaks the gate's tests rather than silently
  changing what is protected.
- `tools/beads_gate.py` now depends on a machine that has `paseo`. CI does not,
  so on CI the check is reported as not judged — the same answer check 1 already
  gives. `tools/test_no_real_queue.py` shadows `bd` and `paseo` on `PATH` for
  exactly this reason, and `--no-paseo` is the deliberate way to rehearse the
  lane without either.
- The cost is that the lane reaches outside the repository. A hook in `bd` would
  not, and this record trades a clean boundary for protection that exists today.
  That trade is the decision, and it is revisable when upstream lands.
- The idle holds it prints are a permanent line on the run's output while agents
  are parked on finished beads, which is a small amount of noise in exchange for
  the two real ones having been found.

## References

- `tools/beads_gate.py` — `holds_lease`, `unleased_beads`, `load_paseo`,
  `reclaimed_lease_findings`, `parked_lease_holds`, `load_reclaim`.
- `tools/test_beads_gate.py` — `ReclaimedLeaseTests`.
- `tools/bd-safe-reclaim.py` — `protect_reason`, `LIVE_STATUSES`, the two halves
  of the hold.
- ADR-0152 (the gate this record adds a check to), ADR-0118 (the lanes and what
  CI runs), ADR-0141 (the generated index this record's row joins).
- Beads `SpatialEngine-8oe` (this record, and the `bd` hook evidence quoted
  above), `SpatialEngine-imz.4` (ADR-0152 and the split), `SpatialEngine-u2x.30`
  (the eight leases).

## Measurements

`tools/beads_gate.py --root . --base origin/main`, on the machine this record
was written on, 2026-10-02:

| Input | Count | Note |
| --- | --- | --- |
| `paseo` agents | 16 | read from `paseo ls --json` |
| Open beads with no lease | 29 | the population the check reads |
| Held by a **running** agent | 0 | no live reclaim outstanding |
| Held by an **idle** session | 2 | `SpatialEngine-xr4`, `SpatialEngine-imz.9` |

The line the gate prints for that run:

```
beads-gate: lease: 29 unleased bead(s) read against 16 paseo agent(s); 2 held by
            an idle session and reported rather than failed (SpatialEngine-xr4,
            SpatialEngine-imz.9)
```

The two idle holds are the finding the bead predicted: without this check they
were visible only by running `bd-safe-reclaim.py` by hand and reading its
verdicts. The counts are live state and move with the swarm — a second run in
the same session read 33 unleased beads against 15 agents and 4 idle holds,
still zero running ones. The reproduction the tests carry is the `running`
case, which neither run contains: one lease is recovered out of band
(`python3 tools/bd-safe-reclaim.py`) rather than synthesised, because the point
of the test is that the check reads the pair, not that this repository is
misbehaving.