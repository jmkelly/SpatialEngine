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
`$(git rev-parse --git-common-dir)/adr-reservations/NNNN.json`, recording the
branch, the bead and the base it was taken against, and published into that
name whole by a hard link. The common dir is the one directory every worktree
of the repository shares, which is where the swarm's branches live — a store
inside a worktree's own git dir would be private to that worktree and would
reserve nothing. The exclusive take is the whole mutual-exclusion mechanism:
the kernel serialises it, so there is no lock file to keep alive and no
daemon. A branch that loses the race is told which branch holds the number and
is handed the next one.

The take is a hard link. The record is written to a temporary file in the
store and linked into place under the final name, so a name a reader can see
is only ever a name whose body is already in it, and the link — which fails
`EEXIST` against a name that already exists — is what makes the reservation
mutual. A rename would not do: `os.replace` overwrites a name that exists, so
it would clobber a live claim and hand the number to two branches.

`--check NNNN` is the gate a worker runs immediately before writing: it is
non-zero when the number is claimed by a record or reserved by *another*
branch, and zero when the branch asking already holds it, so the
reserve-then-recheck flow is idempotent. `--list` names the holder of every
live reservation, `--release` gives the number back, and a reservation goes
stale — reusable — when its branch no longer exists, when its body carries no
`reserved_at` to age, or when it is older than `--max-age-days` (14), because
a worker that dies mid-bead must not hold a number for ever. Stale means the
record was *read* and its holder judged dead. A record this tool cannot read
is an unknown holder rather than an expired one, and is never swept: `--list`
and `--check` name it as held by somebody the tool cannot account for, rather
than printing a branch it does not know. "No longer exists" means no ref
resolves the recorded holder, not that nothing is under `refs/heads`: the
holder is a short ref name and which ref names it depends on the checkout that
recorded it, so a hold whose branch survives as a remote-tracking ref stays
live until it ages out. Looking only under `refs/heads` swept live holds on a
ci runner — `actions/checkout` leaves it with no local branches of its own —
and a sweep is the one thing that hands a number to two branches
(SpatialEngine-ivp). A branch can only release its own reservation: releasing
someone else's is how a collision gets manufactured by hand.

## Amendment (SpatialEngine-u2x.34, 2026-09-30)

Two sentences in the decision above are amended. The decision itself is
unchanged — a number is reserved before its record is written, the take is
exclusive, and a hold whose holder is dead is swept — but the record used to
state as an invariant the one property the implementation did not have.

- The take was an `O_CREAT | O_EXCL` create with the body written afterwards,
  on the reasoning that this left "no window between the check and the write".
  There was one: it ran from the create to the *body* write, and any branch
  reading the store in that window saw an empty file. The take is now a hard
  link of a fully written record, which keeps the exclusivity (the link fails
  `EEXIST`) and closes the window, because the name only ever appears with its
  body already in it.
- A reservation went stale — and was unlinked — also "when the file cannot be
  read". That clause is what turned the first defect into a collision: a
  reader walking past an in-flight claim read the empty file, judged it dead,
  unlinked it, and handed the number to the next branch while the branch that
  had just won it still believed it held one. An unreadable record is now an
  unknown holder and is never swept; stale means the record was read and its
  holder judged dead.

Neither amendment is a new decision, so this record is amended in place rather
than superseded.

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
- SpatialEngine-u2x.34 (the atomic publish and the no-sweep-unreadable rule
  this record was amended to state)
- `AGENTS.md` ("Task queue" — the reservation step a worker runs)
