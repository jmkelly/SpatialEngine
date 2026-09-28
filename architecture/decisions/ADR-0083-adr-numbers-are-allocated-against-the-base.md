---
status: accepted
date: 2026-09-28
deciders: maintainer + agent
---

# ADR-0083: ADR numbers are allocated against the base, not the branch point

## Context

An ADR number is a reference: one number, one record, and every bare
`ADR-NNNN` in the tree resolving to exactly one file. `AdrNumberingTests`
states that rule and fails the build on a duplicate number and on a citation
that resolves to zero or to two records (SpatialEngine-u2x.24).

That enforcement is a merge-time check, and the gap it left is allocation. A
branch picks its number by looking at the base it branched from, and six
branches do that at once. In one coordinator tick (2026-09-28) four of them
claimed numbers main already held: ADR-0081, and ADR-0075 three times, for
three different decisions. Every branch was *green* — verify ran against a base
that had since moved — so the collision surfaced at merge time and the
coordinator renumbered. That is coordinator work that is not coordination, and
the deeper cost is the ambiguous reference the rule exists to prevent: prose
saying "ADR-0075" meant ground-distance buffering in one file and the
feature-query plan in another.

Three ways to close it, in rising cost:

- (a) a helper that reads the numbers already claimed *on the base* and prints
  the next free one, re-run immediately before the record is written;
- (b) number reservation recorded in the bead, so a coordinator tick hands the
  number to the worker;
- (c) dropping the number from the file name and letting a build step assign and
  rewrite every reference.

## Decision

(a). `tools/adr-next-number.py` prints one past the highest number claimed by
`architecture/decisions/ADR-NNNN-*.md` on `origin/main` (falling back to
`main`, then `HEAD`), unioned with the records in the working tree, so the
record a branch has already written counts as claimed. `--check NNNN` exits
non-zero when that number is taken, which is the form a worker can use as a
gate immediately before writing.

Monotonic rather than gap-filling: the lowest unused number is the same hole
for every branch reading the same base, and two of them take it. The tool is
run at the start of a bead that will write a record and re-run if the branch
has been rebased; `eng/verify.sh` runs its tests so the step cannot rot.

## Consequences

- Numbers are chosen against a base, not against a branch point, so the
  collision is caught by an exit code at write time instead of a red gate on
  main. Two branches can still both read the same base and both be handed the
  same number — nothing is reserved — but the second one to write sees
  `--check` fail, and the loser renumbers before merge rather than after.
- The number is still chosen by a human-readable helper, not a lock, so the
  rule stays "re-run it immediately before writing". Reservation (b) is the
  fix if that proves too weak under a coordinator that fans out more than six
  concurrent ADR-writing branches; it costs a tick of coordinator work per
  record.
- The tool reads git, so it needs a checkout with a base ref it can resolve;
  an unknown `--base` is an error, never a guess.
- The cost of a late collision is unchanged: the coordinator renumbers, the
  prose references move with it, and the branch is rebased.

## References

- `tests/architecture/Spatial.Architecture.Tests/AdrNumberingTests.cs`
  (the numbering rule and its enforcement, SpatialEngine-u2x.24)
- `tools/adr-next-number.py`, `tools/test_adr_next_number.py`
- `AGENTS.md` ("Task queue" — the allocation step a worker runs)
