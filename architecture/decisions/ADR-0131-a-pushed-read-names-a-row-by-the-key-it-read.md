---
status: accepted
date: 2026-09-30
deciders: maintainer + agent
amends: ADR-0098, ADR-0124, ADR-0128
---

# ADR-0131: A pushed read names a row by the key it read, and an empty selection reduces to a zero count and no groups

## Context

ADR-0124 pushed the order and the page onto the SQL Server store and, in §8,
wrote the rule the PostGIS reader had been violating:

> The identity is read over the whole schema. The pushed read's shape is the
> plan's projection plus the identity columns the projection did not already
> carry, and the row mapper's identity indexes are the identity columns'
> positions in what was *read*. An ordinary read (no projection) already
> carries them, and taking the appended columns as the identity would name
> every feature by its row ordinal instead of its key.

`PostgisPlanReader.Columns` was taking the appended set. It built the read's
schema as `[projection fields, identity columns not already in the
projection]`, then handed the mapper the positions of the *appended* ones. An
ordinary read projects nothing, so it already carries the primary key column,
so nothing is appended, so the identity is an empty list — and
`PostgisRowMapper.MapRow`'s empty-identity fallback names the row by its
ordinal. A pushed read of a keyed PostGIS table answered `objectIds` `0, 1, 2,
…` whatever the keys were, and the store's own page contract (the ids, the
`Ids` restriction, the edit round-trip — ADR-0097) was wrong wherever the
store pushed a plan.

It was invisible for one reason, and the reason is the more useful half of
this record: **the conformance dataset has no primary key.** `CreateAsync`
creates a table from the sample features and adds no key, so the shared
pushdown-equals-reference suite (ADR-0098) never took the pushed path on
PostGIS at all. The suite compares feature *identities*, and a dataset with no
identity is the one shape in which the pushed and the reference answers are
the same by construction. The SQL Server reader got the identity-carrying
conformance case with ADR-0124 (`SqlServerQueryConformanceTests`); this store
did not.

Adding it (`The_pushed_page_matches_the_reference_over_an_identity_carrying_table`)
measured the pushed path for the first time, and the plan read and the count
passed while **the reduction face did not**. Two answers were wrong, both
about a selection with no rows in it, and both had been written into the
store while no test could reach them:

- **The row count of an empty selection was a null.** `PostgisPlanReader.Groups`
  replaces the dialect's single row with `FeatureReduction.EmptyGroup` when
  that row's row count says nothing was selected, and every spec in it is a
  null. ADR-0098 §3 says the opposite about a count: *"An empty set. An
  ungrouped reduction of an empty set is one group of nulls, not zero groups …
  A count is zero."* SQL's ungrouped `COUNT(*)` already says zero, and the
  store overwrote it.
- **A page past an ungrouped reduction's only group was that group.** The
  `rows.Count == 0` branch answers an ungrouped reduction with the empty group
  because an ungrouped reduction is one group (ADR-0128's page rule). But with
  an `OFFSET` past zero, the dialect returns no row because the *page* cut the
  one group, not because the selection was empty — and the reference, which
  pages the single group in memory, answers no groups.

## Decision

1. **The identity indexes are the identity columns' positions in what was
   read**, not the set of columns appended to it:
   `description.IdColumns.Select(column => read.IndexOf(column)).Where(index => index >= 0)`.
   The same expression, and the same reason, as ADR-0124 §8. An ordinary read
   carries the whole schema and therefore the key; a projected read carries
   the key because the reader appended it for exactly this purpose. Either way
   the mapper is handed the key's own columns.

2. **The identity is a property of the read, and the read is a property of the
   dataset.** A pushed page's features are named by the dataset's identity
   whatever the plan asked for, exactly as a whole table read names them
   (ADR-0097). A plan that cannot reach SQL for its restriction keeps the
   identity the full read gave each row, so the same feature has the same id
   under every plan — which is the invariant the `Ids` restriction and the edit
   round-trip both rest on.

3. **A pushed reduction of a selection with no rows is one group whose values
   are nulls, except the row count, which is a zero.** The row count counts
   rows, not values, so it is the one member of the empty group the dialect's
   answer already was. `COUNT(field)` stays null through
   `FeatureReduction.Counted`, as ADR-0098 §3 requires.

4. **A page of an ungrouped reduction that lands past its one group is no
   groups**, and reports no "one more". The ungrouped reduction is one group
   whatever the cap, so the reference's page over it is either that group or
   nothing; answering the empty group to a page that skipped it would be
   answering a plan the caller did not ask. This narrows ADR-0128's page rule,
   which stated the past-the-last-group case without distinguishing the one
   group from the rest.

5. **The pushed path is measured where it is taken.** The PostGIS conformance
   suite runs a second time over a hand-made keyed table
   (`The_pushed_page_matches_the_reference_over_an_identity_carrying_table`),
   as the SQL Server one does. The key is the fixture's own first field, so the
   discovered schema is the fixture's schema and the identity column is one the
   plan's ordinary read already carries — the exact shape that was untested.
   The store's own page contract over that table is pinned directly too
   (`A_pushed_read_of_a_keyed_table_names_each_feature_by_its_key`), and the
   two empty-selection answers are named in their own test rather than left to
   be found again by the suite.

## Consequences

- A pushed read of a keyed PostGIS table answers its page contract with the
  table's keys. `returnIdsOnly`, an `Ids` restriction, an edit round-trip and
  a paged walk now name the same feature the same way on both SQL providers.
- The shared conformance suite leaves the reference path on this provider, so
  the pushed order, page, projection, count, distinct set, grouped aggregate,
  `having` and group page are all compared against the reference for the first
  time. Three defects were found by doing it; the two reduction ones are
  fixed here and the third (the identity) is the subject of the record.
- `CreateAsync`'s keyless table stays the fixture for a store with no identity
  to push, and the keyed table is the fixture for one that has. Neither is the
  whole story, and a suite that only ran the first was measuring a fallback.
- The empty-group rule is now the store's own rather than `FeatureReduction`'s
  (`FeatureReduction.EmptyGroup` nulls every spec, which is right for a
  *group* of a grouped reduction and wrong for the ungrouped row count). The
  two rules are one line apart and are written out in the reader where the
  pushed dialect's answer is translated.

## Rejected

- **Name a pushed row by its ordinal whenever the identity column was already
  read.** It is self-consistent — the id is stable within one read — and it is
  a different feature: the same row is `0` under one plan and `3` under the
  next that appends the key.
- **Append the identity columns unconditionally, so the identity is always
  the appended set.** One more column on every read, a duplicate column the
  mapper would then have to disambiguate, and the same defect for the ordinary
  read in a new shape.
- **Read the identity out of the projection's own fields when it happens to
  carry them.** That is §1 restated with a lookup the dataset description
  already answers, and it would name a projected read by whichever projected
  field happened to share the key's name.
- **Fix the two reduction answers in the shared reference instead.** They are
  the reference's own answer already: ADR-0098 §3 says a count is zero, and
  the reference reports the row count of an empty selection as
  `members.Count`. Nothing in the reference is wrong.
- **Leave the conformance case out and file the two reduction defects
  separately.** The case is the measurement; without it the pushed path on this
  provider is unmeasured, which is the condition that let three defects sit
  in one reader. They are one reader and one rule each, and they are fixed
  with the case that found them.

## References

- Principles 6 (contracts outlive implementations), 10 (stores are providers),
  15 (pushdown optional, semantics-preserving), 17 (small kernel).
- ADR-0074 §4-6 (the plan, the reference executor, the reduction faces),
  ADR-0097 (pushdown, literal binding, and the identity a restriction may not
  renumber), ADR-0098 §3 (the ordering rule, the empty set, the shared
  conformance suite), ADR-0116 §1-2 (the paged read), ADR-0124 §8 (the rule
  this record applies to the second store), ADR-0128 (the group page and
  `having`, whose page rule §4 narrows), ADR-0127 (the composite order).
- `src/Spatial.Stores.PostGIS/PostgisPlanReader.cs` (`Columns`, `Groups`,
  `EmptyGroup`), `src/Spatial.Stores.PostGIS/Core/PostgisRowMapper.cs`,
  `src/Spatial.Stores.SqlServer/SqlServerPlanReader.cs` (the same rule, first
  written),
  `tests/integration/Spatial.PostGIS.Tests/PostgisQueryConformanceTests.cs`,
  `tests/integration/Spatial.SqlServer.Tests/SqlServerQueryConformanceTests.cs`.
