---
status: accepted
date: 2026-10-02
deciders: maintainer + agent
summary: The DE-9IM patterns the Esri surfaces serve are read twice, from two independent directions, and neither reader replaces the other: `SpatialRelationMatrix` (SpatialEngine-dih) is where a fixture and its expected verdict are written down once, so the Feature Service query path and the Geometry Service `relation` operation are held to the same row — but it is the served table's own second copy, and a table cannot cross-check itself. The oracle in `FeatureSpatialRelationTests` (SpatialEngine-imj) derives each pair's matrix from nine single-cell questions and reads the served patterns over that derivation. The oracle is rebased onto the table rather than folded into it, and making it read the table's hand-written `De9im` column found two of the three line-along-the-edge rows wrong — which is the argument for it, made rather than asserted.
amends: ADR-0036
---

# ADR-0156: The DE-9IM patterns have two independent readers

## Context

Two beads implemented the same idea a week apart and landed out of order.
SpatialEngine-dih (merged `f4a8de9`) added `SpatialRelationMatrix`, one
fixture list and one verdict table behind both Esri surfaces, after
SpatialEngine-zpz and SpatialEngine-51k had each served a verb from their own
copy of a pattern table. SpatialEngine-imj (`c4090137`, still open) added
`Spatial.Core.Geometry.De9imPattern` — one grammar, read by the verb and by the
boundary — and an oracle that answers every pattern the adapter serves from the
pair's **exact intersection matrix**, derived from nine single-cell questions,
rather than by asking the matcher the same pattern again.

Rebasing imj onto post-dih `main` conflicts in
`FeatureSpatialRelationTests.cs`, and the merge tool escalates rather than
resolving a conflict it cannot read as textual
(`tools/bd-merge-bead.py`: *escalate, do not resolve silently*). So the
question has to be answered before imj can merge, and it is a question about
what the tests are *for*.

The tempting answer is that dih supersedes imj: dih's table is the one place a
DE-9IM expectation is written, so the oracle is a second source of truth in the
same file. That answer is wrong, and it is wrong for a specific reason. dih's
table holds the **hand-computed** matrix and the **hand-written** verdict
columns. It is checked by the two surfaces agreeing with it — which is a check
that the adapter still serves what it served. It cannot check that the values
written in it are right, because a table cannot cross-check itself, and its own
`De9im` column was, at the moment of the decision, read by nothing at all.

## Decision

**Keep both readers, rebase the oracle onto the table, and let the oracle check
the table's hand-written column.**

- `SpatialRelationMatrix` stays where dih put it: one fixture definition, one
  verdict row, and both Esri surfaces reading it. It is not split, and the
  query path's fixtures are not moved back out of it.
- The oracle stays a separate test in `FeatureSpatialRelationTests`, written
  **against** `SpatialRelationMatrix` for everything it can take from it — the
  fixture geometries and the pair table come from `SpatialRelationMatrix.Pairs`
  and `SpatialRelationMatrix.Of` — and deriving only the matrix itself, which is
  the one thing no table can hand it.
- The oracle reads the served patterns by spelling them out where the OGC states
  them, rather than importing the served constants. That is what makes it an
  oracle: importing `SpatialRelationPredicates.ContainsPattern` would make it a
  comparison of the served table with itself.
- The oracle also checks `SpatialRelationMatrix`'s `De9im` column against the
  matrix it derives, cell by cell, over every row the table carries. The
  comparison is `T`-tolerant, because the column writes a non-empty cell as `T`
  whatever its dimension — it states a row, not a matcher — and a test that
  compared the strings outright would fail on all fifteen rows and say nothing.
- Neither test is deleted, and the oracle is not folded into the table as
  another column.

The rebase is not a textual merge, so the resolution is the union of the two
sides plus one sentence in each doc comment saying which reader is which; it is
recorded here because the alternative resolutions (b) and (c) are rejected for
reasons below, not because merging them was difficult.

## Alternatives

- **(b) dih supersedes imj; imj closes as superseded, keeping only the
  `De9imPattern` grammar fix.** This loses the only reader that does not go
  through the served table, and ADR-0036's DE-9IM fix ships with no independent
  verification. The defect class it catches is the one this repository has
  actually hit: a served pattern naming a different cell than the one it means.
  Rejected because it removes the check on the strength of a tidy file layout.
- **(c) Merge the engines and adapters first, rebase the test layer last.**
  That makes the coordinator's job smaller by deferring the conflict, but it
  splits one bead's work across two merges and leaves `main` briefly carrying
  `De9imPattern` with no test of it. Rejected: the same work, in a worse order,
  for the same result.
- **Keep both, and additionally hold the served table to the reference
  implementation's named predicates over every pair.** dih already does this for
  the pair the matrix covers. It is a weaker check than the oracle, because NTS's
  predicates and the patterns this engine serves are the same family's reading of
  the same standard — a shared misunderstanding would agree with itself. Not
  rejected, just not sufficient, and kept where it already is.

## Not decided

Whether the `De9im` column should be normalised to digits throughout now that
it has a reader, or left in its `T`-for-non-empty spelling because that is how
the OGC's own dimension-free relations read. What would settle it: a decision on
whether the column is a display of the matrix or the matrix itself. Tracked as
its own bead; it changes no verdict.

Whether the oracle should walk the pairs whose vertices touch exactly, where
DE-9IM's own conventions about boundary contact are the interesting part. It
does today, which is why `point-vertex` has no verdict row and is matched
against the reference's predicates instead.

## Consequences

- Two tests answer "is this DE-9IM right?" and they are kept apart on purpose:
  one asks whether the adapter serves what the table says, the other asks
  whether the table is right. Merging them into one would make the second
  question unaskable.
- The oracle costs nine to twelve `Relate` calls per pair over 256 pairs per
  run, on top of the pairs' relation calls. It is unit-test work against
  in-memory fixtures, not a runtime cost.
- A hand-written fixture table in this repository is now checked against
  something that did not write it. **That check found a defect on its first
  run:** `line-edge` and `line-collinear` both carried `FF2101102`, the row
  copied from `line-shifted-collinear`, which is right for that row and wrong
  for both of the others in a different cell (`FF2101FF2` — nothing of the line
  is outside the square; `FF21F1102` — the boundaries never meet). Every verdict
  column was correct, so nothing failed; the wrong matrix sat in the row every
  reader of the table trusts. The two rows are corrected here and pinned by the
  test that found them.
- The general lesson is the reason this record exists: a table that is only ever
  read by the code it describes is documentation, and documentation that is
  checked only for self-consistency agrees with itself.

## References

- ADR-0036 (the DE-9IM relation verbs, the pattern table, the grammar and the
  vocabulary gap; this record amends its reading of how the patterns are
  checked)
- ADR-0053 (test-first: a defect gets a failing reproduction before the fix)
- SpatialEngine-u2x.60 (this decision), SpatialEngine-dih (the one table),
  SpatialEngine-imj (the oracle and the grammar), SpatialEngine-u2x.2
- `tests/unit/Spatial.Adapter.GeoServices.Tests/SpatialRelationMatrix.cs`,
  `tests/unit/Spatial.Adapter.GeoServices.Tests/FeatureSpatialRelationTests.cs`,
  `src/Spatial.Adapter.GeoServices/SpatialRelationPredicates.cs`
