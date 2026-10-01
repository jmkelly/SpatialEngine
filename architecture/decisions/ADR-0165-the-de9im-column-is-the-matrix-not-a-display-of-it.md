---
status: accepted
date: 2026-10-02
deciders: maintainer + agent
summary: **The `De9im` column of the hand-computed fixture table is the pair's exact intersection matrix, so every cell carries its dimension (`F`, `T`, `0`, `1`, `2`) and the oracle compares the column outright** — the `T`-for-non-empty spelling and the tolerance that read it are both withdrawn. Twelve of fifteen rows were written in the `T` spelling and are now stated in digits; the derived matrices did not move, so no verdict changed. A display is free to abbreviate and was never what the column was read as: it is read by a test that has the exact matrix in hand, which makes the abbreviation a place where a dimension could have been wrong and would not have been noticed.
amends: ADR-0156
---

# ADR-0165: The De9im column is the matrix, not a display of it

## Context

ADR-0156 decided that `SpatialRelationMatrix`'s hand-written `De9im` column
is read by the oracle in `FeatureSpatialRelationTests`, which derives each
pair's exact matrix from nine single-cell questions. It left one question open,
naming it: whether the column should be normalised to digits throughout now
that it has a reader, or left in its `T`-for-non-empty spelling "because that is
how the OGC's own dimension-free relations read". What would settle it, it said,
was a decision on whether the column is a display of the matrix or the matrix
itself.

The question was not academic, because the two spellings were mixed *within*
the table. `square-equal` was written `TFFFTFFFT` — `T` for every non-empty
cell whatever its dimension — while the three line-along-the-edge rows spelled
the same kind of cell as `FF2101FF2` and its relatives. The mixed spelling is
what made the comparison `T`-tolerant, which is right for a column that means
"non-empty" and quietly weak for one that means "this dimension": a row that
recorded a curve as `T` where the intersection is a point passed, and so would a
row recording an area as `T` where the intersection is empty of dimension 2 but
non-empty — no cell can be `T` and empty, so the failure mode is narrower than
it looks, but it is exactly the failure the column was added to catch.

ADR-0156's own Consequences are the evidence for not leaving the table this
way: the first run of the reader found two of fifteen rows wrong, both in the
`T` spelling, and both invisible to every other reader.

## Decision

**Spell the matrix's dimensions throughout the `De9im` column, and compare the
column to the derived matrix outright.**

- Every cell of the column carries its dimension: `F` for empty, and `T`, `0`,
  `1` or `2` for a point, a curve or an area. No cell is written `T` "for
  non-empty" — `T` is a dimension here, as it is in the OGC's pattern
  grammar.
- `FeatureSpatialRelationTests.Every_hand_computed_matrix_is_the_matrix_the_pair_derives`
  compares the column to the derived matrix with `Assert.Equal`. The
  `T`-tolerant `Records` helper is deleted; there is nothing left for it to
  permit.
- The row and class documentation says the column is the exact intersection
  matrix and stops describing `T` as a display of non-emptiness.

## Alternatives

- **Keep the tolerance and document the column as a display.** The cheaper
  change, and the one ADR-0156 pointed at as legitimate. Rejected because the
  column's only reader has the exact matrix in hand and then throws the
  dimensions away; a display may abbreviate, and this column is not read by a
  human, it is read by a test that could be strict.
- **Normalise to digits but keep the tolerant comparison.** That would leave
  the table half-decided — the column says one thing and the comparison allows
  another — and would keep the failure mode ADR-0156 documented as the reason
  for the reader. Rejected on the same grounds.
- **Drop the `De9im` column and let the oracle derive it.** The table would
  lose the hand-computed expectation entirely, and the two surfaces would be
  held to nothing; ADR-0156's whole argument is that one table and one
  derivation check each other. Rejected.
- **Fold the column's dimension into the verdict columns** — write
  `Contains(2FF…)` rather than a separate column. Not considered further: it
  changes what both Esri surfaces read, which is outside this bead and outside
  what the open question asked.

## Not decided

- **Whether the oracle should also walk the pairs whose vertices touch
  exactly.** Carried forward unresolved from ADR-0156, where `point-vertex`
  has no verdict row for exactly this reason and is matched against the
  reference implementation's predicates instead.

## Consequences

- Twelve of the fifteen rows changed text and none of them changed meaning: the
  matrices the oracle derives are unchanged, so every verdict still holds and
  every served pattern still agrees. The change is in what a reader of the
  table can see — `212FF1FF2` says the inner square's boundary meets the
  outer's interior as a curve, where `TTTFFTFFT` said only that it meets it.
- The check is strict, so the class of defect the tolerance hid — a dimension
  wrong in a cell that is non-empty either way — is now a failing test rather
  than a silent agreement. That is the whole point of ADR-0156's reader, and it
  was unavailable while the column was half digits and half `T`.
- The cost is that the table no longer reads as cleanly as a
  dimension-free summary, and a reader looking for the geometry rather than the
  digits has to remember that `2FFF1FFF2` is "equal" and `FF2101FF2` is "touches
  along an edge". The tables in both files now spell the digits, so this is the
  one place in the test suite where the DE-9IM matrix is written out, and it is
  written out once per file.
- Nothing outside `tests/unit/Spatial.Adapter.GeoServices.Tests` changes. No
  served pattern, no contract and no adapter behaviour moves, and the served
  `T`-tolerant matching in `SpatialRelationPredicates` is untouched — a pattern
  that asks for `T` still matches any non-empty cell, which is the OGC's own
  reading and is a question about the matcher rather than about this column.

## References

- ADR-0156 (the two independent readers, the oracle that reads the column, and
  the open question this record closes), ADR-0036 (the DE-9IM patterns and the
  grammar), ADR-0053 (test-first: a defect gets a failing reproduction before
  the fix)
- SpatialEngine-1pi (this decision), SpatialEngine-dih (the one table),
  SpatialEngine-imj (the oracle and the grammar)
- `tests/unit/Spatial.Adapter.GeoServices.Tests/SpatialRelationMatrix.cs`,
  `tests/unit/Spatial.Adapter.GeoServices.Tests/FeatureSpatialRelationTests.cs`
