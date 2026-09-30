---
status: accepted
date: 2026-09-30
deciders: maintainer + agent
summary: A composite order applies every requested key in turn, each one a tie-break over the keys before it, with the feature identity last.
amends: ADR-0098, ADR-0124
---

# ADR-0127: A composite order applies every requested key in turn

## Context

`FeaturePlanExecutor` is the reference semantics of a feature-read plan
(ADR-0074 §4): every store's pushdown is measured against it, and the
conformance suite runs a store's own SQL answer and the reference's answer side
by side (ADR-0098 §3). Its documented pipeline is "order (the requested keys,
then the feature identity as the contract's mandatory tie-break)", and
ADR-0098 §3 states the same: a store appends the identity as a final ascending
key **over the requested keys**.

`Order` did not compute that. It walked the plan's `OrderTerms` and called
`Sort` — `OrderBy`/`OrderByDescending` — once per term, so every key after the
first discarded the keys already applied and only the **last** requested key
ordered the result. `ThenSort` sat immediately below, with a comment saying
exactly why it is a *then*-key: "an `OrderBy` here would discard the keys
already applied, and the tie-break would become the only order" — the loop above
it did precisely that, and the last term's own sort made the tie-break comment's
warning true for the requested keys too.

The plan the contract describes and every pushed dialect writes — PostGIS puts
all the terms into one `ORDER BY` (`PostgisPlanQueries.Order`), T-SQL the same
(`SqlServerPlanQueries.Order`) — was therefore *not* the order the reference
computed. Over the conformance fixture, a plan of `[category asc, score desc]`:
the reference answered `3, 4, 5, 6, 2, 1` (the score's descending order, nulls
first, with identities breaking the tie) where the contract's order is
`3, 4, 6, 2, 1, 5` (category ordinal, nulls last). Every store that pushed the
plan would have failed the suite's composite case on the strength of the
reference's own answer.

It was invisible because no conformance dataset carries an identity column:
`CreateAsync` makes a table without a primary key on both providers, so a plan
over it has no tie-break to append, the order is never pushed, and the store
and the reference both answer the same (last-key) order. A dataset with a key —
an ingested one, or a hand-made table — is what exposes it, and ADR-0124 §7 had
already found the consequence and worked around it: the SQL Server reader
refused to push *any* composite order, because pushing one would "answer a
different question from the one this store answers today".

## Decision

**Every requested key orders the plan, in the order the plan names them, each
one a tie-break over the keys before it; the feature identity is the last
ascending tie-break over all of them.**

`Order` applies the first term with `OrderBy`/`OrderByDescending` and every
later term — requested keys and the identity alike — with
`ThenBy`/`ThenByDescending`, over the same `AttributeValueComparer`, so the
value rules are unchanged (nulls last ascending, first descending; strings by
bytes, ADR-0121). The reference's order is now the order the contract's
pipeline describes and the order every dialect already writes, so a pushed
composite plan and the reference answer the same sequence.

**ADR-0124 §7's accommodation is withdrawn.** `SqlServerPlanReader.Pushed` is
what a plan can address in T-SQL with `OFFSET`/`FETCH NEXT`, and the reason it
refused a second key is gone: the pushed order and the reference's order are
the same order, so a composite plan is a capped read like every other ordered
plan. The rule is one clause again — the order must be one this table can make
total — which is the rule ADR-0124 §6 states and PostGIS already applies.

## Consequences

- Every store's answer for a composite order changes, and the new answer is
  the contract's: the keys in sequence rather than the last key's. A caller
  that read a layer ordered by two keys and relied on the last key dominating
  sees a different sequence, which is the point — the sequence it was getting
  was never the one the plan asked for.
- A composite-ordered plan is pushed on SQL Server, so a large layer ordered by
  two keys is a page rather than a materialisation. The narrowing ADR-0124 §6
  still holds, and it is a dialect reason: an *unordered* plan is still
  finished in process, because an `OFFSET` needs an `ORDER BY` to skip over.
- The reference's own rules are pinned on their own, in
  `ReferencePlanSemanticsTests`: reversing the terms reverses the answer, which
  a re-sort per key cannot do. A bug in the reference is invisible to the
  conformance suite by construction — the suite compares a store against the
  reference, so both being wrong the same way is a pass.
- The pushed composite order is measured over an **identity-carrying** table in
  both providers' integration suites, because a table without a key never
  reaches the `ORDER BY` at all. The PostGIS case compares the rows' own values
  rather than their feature identities: the PostGIS reader names a pushed row
  by its ordinal whenever the identity column is one the plan already reads
  (SpatialEngine-u2x.55, a separate defect in that reader's shape), which is
  outside this record and would otherwise mask the order being measured.
- Nothing crosses a wall. The change is in `Spatial.Querying` (an
  implementation project) and in the SQL Server reader, and the ordering rule
  itself is unchanged — only which keys survive it.

## Rejected

- **Make `Sort` re-apply the earlier keys.** Same answer, more work: the
  already-ordered sequence is exactly what `ThenBy` extends, and re-sorting by
  the full key vector is the definition of a stable multi-key sort spelled the
  long way.
- **Leave the reference and push the last key's order into the dialects
  instead.** ADR-0124 rejected it in the same words this bead uses, and for
  the same reason: it would keep the store self-consistent with a broken
  reference and write the defect into every statement the provider issues.
- **Stop the identity tie-break from being a `ThenBy` when the plan has more
  than one key.** The tie-break is over the *requested keys*, so it is a
  then-key by definition; a composite plan with no tie-break would be a
  non-deterministic order, which is the one thing a page boundary cannot be
  cut on.

## References

- Principles 10 (stores are providers), 15 (pushdown optional,
  semantics-preserving).
- ADR-0074 §4 (the plan, the reference executor), ADR-0098 §3 (the ordering
  rule), ADR-0116 §1 (the paged read), ADR-0121 (ordinal comparisons in
  pushdown), ADR-0124 §6-§7 (the paged SQL Server read, and the accommodation
  this record withdraws).
- `src/Spatial.Querying/FeaturePlanExecutor.cs` (`Order`, `Sort`,
  `ThenSort`), `src/Spatial.Stores.SqlServer/SqlServerPlanReader.cs` (`Pushed`).
- `tests/unit/Spatial.Stores.Memory.Tests/ReferencePlanSemanticsTests.cs`,
  `tests/unit/Spatial.Stores.SqlServer.Tests/SqlServerPlanPagingTests.cs`,
  `tests/integration/Spatial.PostGIS.Tests/PostgisQueryConformanceTests.cs`.
