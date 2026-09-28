---
status: accepted
date: 2026-09-28
deciders: maintainer + agent
---

# ADR-0089: ADR numbers are reserved before the record is written

## Context

An ADR number is a reference: one number, one record, and every bare
`ADR-NNNN` in the tree resolving to exactly one file. `AdrNumberingTests`
states that rule and fails the build on a duplicate number and on a citation
that resolves to zero or to two records (SpatialEngine-u2x.24, extended to the
distilled register by SpatialEngine-u2x.31).

That enforcement is a merge-time check, and the gap it left is allocation. A
branch picks its number by looking at the base it branched from, and six
branches do that at once. In one coordinator tick (2026-09-28) four of them
claimed numbers main already held: ADR-0081, and ADR-0075 three times, for
three different decisions. Every branch was *green* — verify ran against a
base that had since moved — so the collision surfaced at merge time and the
coordinator renumbered. That is coordinator work that is not coordination, and
the deeper cost is the ambiguous reference the rule exists to prevent: prose
saying "ADR-0075" meant ground-distance buffering in one file and the
feature-query plan in another.

Reading a better base is not a fix. It narrows the window; it does not close
it, and this record is the evidence: written against ADR-0088, it was renumbered
twice while it was in flight — once because a helper run an hour earlier had
handed out a number main took in between, and once because a parallel branch
reserved and merged the same number under the new mechanism's own rules. Both
renumberings are mechanical, and neither was a merge-time surprise.

Three ways to close it, in rising cost:

- (a) a helper that reads the numbers already claimed *on the base* and prints
  the next free one, re-run immediately before the record is written;
- (b) number reservation, so a branch *holds* the number it is about to write
  and any other branch is told, at allocation time, that it is gone;
- (c) dropping the number from the file name and letting a build step assign
  and rewrite every reference.

## Decision

(a) and (b), in that order, because (b) needs a claim set and (a) is the claim
set. `tools/adr-next-number.py` prints one past the highest number claimed by
`architecture/decisions/ADR-NNNN-*.md` on `origin/main` (falling back to
`main`, then `HEAD`), unioned with the records in the working tree, so the
record a branch has already written counts as claimed. Monotonic rather than
gap-filling: the lowest unused number is the same hole for every branch reading
the same base, and two of them take it.

`--reserve` then *takes* the number. The reservation is a file at
`$(git rev-parse --git-common-dir)/adr-reservations/NNNN.json`, created with
`O_CREAT | O_EXCL`, recording the branch, the bead and the base it was taken
against. The common dir is the one directory every worktree of the repository
shares, which is where the swarm's branches live — a store inside a worktree's
own git dir would be private to that worktree and would reserve nothing. The
exclusive create is the whole mutual-exclusion mechanism: the kernel
serialises it, so there is no lock file to keep alive, no window between the
check and the write, and no daemon. A branch that loses the race is told which
branch holds the number and is handed the next one.

`--check NNNN` is the gate a worker runs immediately before writing: it is
non-zero when the number is claimed by a record or reserved by *another*
branch, and zero when the branch asking already holds it, so the
reserve-then-recheck flow is idempotent. `--list` names the holder of every
live reservation, `--release` gives the number back, and a reservation goes
stale — reusable — when its branch no longer exists, when the file cannot be
read, or when it is older than `--max-age-days` (14), because a worker that
dies mid-bead must not hold a number for ever. A branch can only release its
own reservation: releasing someone else's is how a collision gets manufactured
by hand.

## Consequences

- A parallel branch is stopped at allocation rather than at merge, and the
  message names the holder, so the loser renumbers itself. The coordinator
  stops renumbering.
- Allocation is now stateful, and that state is a file outside the working
  tree. A worker that dies leaves a reservation until the branch is deleted or
  it ages out; `--list` is how a worker finds out it is standing on a dead
  hold. Reservations are development infrastructure, so they carry no ADR of
  their own and are never committed.
- The store is shared per repository checkout, not per machine. Two clones on
  two machines reserve independently and can still collide; the remedy there
  is the coordinator reserving numbers itself, which costs a tick per record.
  Nothing about the numbering rule changes, only how early the collision is
  found.
- `--reserve` is advisory in the sense that a branch can still write a record
  without reserving: nothing hooks it into `eng/verify.sh`. What the gate
  *does* hold is that the allocator agrees with the tree —
  `AdrNumberingTests` runs it and fails if the number it hands out is claimed,
  or if `--check` clears a number a record already holds — so a broken
  allocator is caught by CI rather than by a collision.
- The number is still chosen by a helper rather than a lock service, so the
  documented flow stays "reserve at the start of the bead, re-check
  immediately before writing". Reservation in the bead is the fallback if that
  proves too weak under a coordinator fanning out more than six ADR-writing
  branches at a tick.

## References

- `tests/architecture/Spatial.Architecture.Tests/AdrNumberingTests.cs`
  (the numbering rule and its enforcement, SpatialEngine-u2x.24; the register
  duplicate guard, SpatialEngine-u2x.31; the allocator/tree agreement)
- `tools/adr-next-number.py`, `tools/test_adr_next_number.py`
- `AGENTS.md` ("Task queue" — the reservation step a worker runs)
