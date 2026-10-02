---
status: accepted
date: 2026-10-02
deciders: maintainer + agent
summary: The ADR index carries a **generated reading order** — every family the `amends:` links describe, read from its root, with each record that has since been narrowed marked by the records that narrow it — and the two link fields that make a family derivable are gates: a `(amends NNNN, …)` clause in a `summary` the front matter does not carry is a finding, and a hand-written `amended-by:` that contradicts the derived list is a finding.
amends: ADR-0141, ADR-0150
---

# ADR-0159: The family is a reading order, derived from the `amends:` links

## Context

ADR-0141 made the register and the index generated artefacts, and ADR-0150
made the shape of a record a gate. ADR-0150 also derived an **Amended by**
list from each record's own `amends:` field, and said why that was not the
whole answer:

> so a family over one topic is a row here rather than a reading order the
> reader has to assemble across eleven files

It is still a row. Nothing in the index says which of a family's records to
open first, and nothing says which of them a later record has narrowed — which
is the question a reader actually has when a subject spans a dozen records.

The family the bead named is worse than that. It was measured as ADR-0117 plus
ten records, and **nothing amends ADR-0117**: the tile cache record is not the
root of the store-pushdown family, and the ten records named beside it do not
refine it. The real root is **ADR-0074**, the feature-query plan, and the
family is sixteen records and 30,348 words — 20% of the corpus:

- the plan and its two pushdown rules (ADR-0074, ADR-0097, ADR-0098);
- the collation chain, one record per statement the order touches: the sort
  keys (ADR-0121), the `WHERE` (ADR-0123), the identity restriction
  (ADR-0126), the store's own sidecar (ADR-0130), the search fold (ADR-0132),
  the column's declared collation (ADR-0136);
- the shape of a pushed read: order and page on T-SQL (ADR-0124), the
  composite order (ADR-0127), the reduction's page and `having` (ADR-0128),
  the key a pushed row is named by (ADR-0131), the reductions (ADR-0133), the
  percentile (ADR-0137) and the geometry aggregate (ADR-0157).

Half of those sixteen declared **no** `amends:` link at all. ADR-0121 states in
its Context that it decides what ADR-0098 §3 left implicit; ADR-0123 that it
does for the predicate compiler what ADR-0121 did for the sort keys; ADR-0126
the same for the identity restriction; ADR-0132 writes the links in its
`summary` and nowhere else. A derived index cannot see a refinement its records
do not declare, so the derived list was a list of the twenty declared links and
not of the family.

The twenty-seven `amends:` links with no reciprocal `amended-by` were measured
in ADR-0150's worktree, which rejected *requiring* the reciprocal field. The
derived list makes reciprocity unnecessary — but seven records still carry a
hand-written `amended-by:`, and nothing compared it with what the generator
derives. Two of the seven had already drifted: ADR-0086 said it was amended by
ADR-0153 alone while ADR-0111 amends it too.

## Decision

**The index carries a generated reading order for every family, and the links a
family is derived from are gates.**

- A **family** is every record reachable from a root through `amends:`. A
  record belongs to the family of the **earliest** record it refines, so the
  families partition the corpus and no record is rendered under two roots.
  `tools/arch-index.py` renders the families of three or more records — below
  that the "Amended by" row already says everything — in `architecture/
  decisions/README.md`, which is where the index already links from.
- Within a family the order is the **root first, then by number**: a refiner
  narrows what it reads, so it is read after it. Every record with refiners is
  marked `*(narrowed by …)*` and carries its own record count, word count and
  share of the corpus, so the reader sees that the first record is the root of
  sixteen before opening it.
- `undeclared_refinements` is a finding: a `(amends NNNN, NNNN)` clause in a
  `summary` that the front matter does not carry is a link the register claims
  and the index cannot see. `(amends nothing; follows …)` declines to amend and
  matches no list.
- `reciprocity_findings` is a finding: a hand-written `amended-by:` that is not
  the derived set of refiners contradicts the index. The field stays allowed —
  seven records carry one and no rule retires it here — but it can no longer
  disagree with what the records say.
- Twelve records gain the `amends:` link their own prose already states, and
  two reciprocals are corrected. **No decision text changes**, nothing is
  renumbered, merged or rewritten, and no record is deleted: the family is
  answered by an index and a front matter, not by a smaller corpus.

## Alternatives

- **Renumber or merge the family into three records.** The 122-file diff
  ADR-0150 declined to arrive as, and the preference order is explicit: an
  index that answers the question beats a smaller corpus. The reading order
  answers it in one generated section.
- **Require `amended-by:` on every `amends:` link** (ADR-0150's rejected
  option, 27 links). The index derives the other end, so the copy would be a
  second place to forget; the gate instead checks the copies that exist.
- **Retire `amended-by:` by name**, as the retired status schema was. Cleaner,
  but it edits seven records' front matter to remove a field nothing reads,
  for no gain over checking it.
- **A hand-written reading order in the distilled digest.** A digest says what
  is true; it does not decide what is true, and a hand-maintained list of
  sixteen records in the order they were decided drifts on the next amendment.
- **Collapsing two records that look alike.** ADR-0121 and ADR-0123 are the same
  defect in a different statement (a pushed comparison inheriting the
  database's collation), but they are different statements — the sort keys and
  the predicate — and a store that fixed one has still lost row sets. ADR-0126
  and ADR-0130 are the same rule in a different place: an *authored* identity
  column and the store's *own* sidecar, which is why the second is a
  declaration and the first a restatement on every statement. Neither pair is
  a record stated twice, so nothing is merged.

## Not decided

- **Whether a record's `Context` may cite a decision it refines.** The gate
  reads the `(amends …)` clause in the `summary`, which the corpus writes
  consistently, and not prose that says "ADR-0121 made every string comparison
  the provider writes …". Reading that mechanically is a judgement about
  English; a narrower rule would need a convention narrower than "mentions it
  in the right tone". A convention in the template is the cheap version and is
  not decided here.
- **Ordering a family by dependency rather than by number.** Number order is
  the order the decisions were taken, and every corpus refiner is numbered
  above what it refines. A topological order would read the same today and
  would be the honest answer if a record ever refines an earlier number.

## Consequences

- Opening ADR-0074 answers "how does a pushed-down string comparison work"
  without a grep: the plan, the pushdown rules, the store surface, then the
  collation chain one statement at a time, with the records that narrowed each
  one named beside it.
- The index grew by about 90 lines and is still generated, so a family cannot
  drift from the records the way a hand-written list would.
- The corpus's `amends:` front matter is now load-bearing in three places —
  the register's routing, the "Amended by" list and the reading order — so an
  undeclared refinement is a gate failure rather than a missing line. A record
  written below this number that refines another and says so only in prose is
  invisible to all three; its `summary` is the place to say so, and the gate
  reads it.
- `corpus_findings` grew two checks, so `tools/doc-freshness.py` reports them
  without a second implementation (ADR-0141). `arch-index.py --register` reads
  them, so the documentation audit picks them up for free.
- The cost is that adding a record which refines another now fails the gate
  until the front matter link exists, and fixing it means a second thought
  about which record it refines. That is the intended direction of the work.

## References

- `architecture/decisions/README.md` — the generated **Reading order**.
- `tools/arch-index.py` — `reading_order_families`, `render_reading_order`,
  `undeclared_refinements`, `reciprocity_findings`, `MIN_FAMILY_SIZE`.
- `tools/test_arch_index.py` — `ReadingOrderTests`, `ReciprocityTests`.
- ADR-0141 (the register and index are generated and gated), ADR-0145 (the
  register is generated wholesale), ADR-0150 (the shape rule; the follow-up
  this record is).
- Beads `SpatialEngine-vl1` (this record), `SpatialEngine-6l6` (ADR-0150).
