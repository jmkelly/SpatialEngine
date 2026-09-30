---
status: accepted
date: 2026-10-01
deciders: maintainer + agent
summary: A **decision record is one decision, and its shape is a gate**: a closed section set (Context, Decision, Consequences required and in order; Alternatives, Not decided, References, Measurements allowed and nothing else), a 1500-word budget on the narrative with References and Measurements exempt because a record is long when it carries measurements that may be load-bearing, an `over-budget:` front-matter field a record over the budget must fill and a record inside it may not, and a rule that binds from ADR-0150 forward rather than the 135 records that predate it. A record that refines another says so in `amends:`; the generated index derives the other end of every such link, so a family over one topic is a row rather than a reading order the reader assembles across eleven files. Enforced by `tools/arch-index.py --check`, which every lane runs, and reported as `adr-shape` by the documentation audit from that one implementation (SpatialEngine-6l6).
---

# ADR-0150: A record is one decision, and its shape is a gate

## Context

The corpus of decision records grew to 135 in under a month, and nothing held
a line on what one of them looks like. Three defects, measured below; the third
shaped the answer more than the other two.

**There is no template.** Every writer reached for the section vocabulary
independently, so the corpus has five agreed sections and 36 records that carry
a heading outside them — `Sources`, `See also`, `Rejected`, `§applied`,
`§licence`, and two records whose headings are `Merge note (SpatialEngine-u2x.8)`
and `Amendment (SpatialEngine-u2x.34, 2026-09-30)`. "Alternatives in 51 of 135"
read as optional-by-accident rather than chosen.

**Nothing checks a record's size, section set or shape.**
`AdrNumberingTests` checks that a number identifies one record and that the
allocator agrees with the tree, and no gate fails on a 2,400-word record.

**The family is only reachable by grepping.** The store-pushdown family — ADR-0117
plus the ten records that refine it (0121, 0123, 0126, 0127, 0128, 0129, 0130,
0131, 0132, 0133) — is a reader's problem: a person who wants to know how a
pushed-down string comparison works assembles a reading order across eleven
files, and ADR-0121 is cited by 43 of the other records. The metadata to
discover that family exists and is generated: `amends` and `amended-by` are
front matter (ADR-0141) and the index renders them. What is missing is that the
link is **one-directional in practice** — 27 of the 39 `amends` links have no
reciprocal `amended-by` — so a reader who starts at the amended record has no
row telling them it was refined.

And the corpus's long records are long for a good reason. ADR-0134 and ADR-0118
carry measurements, and the measurements are load-bearing: the fast lane's
scoping numbers are the argument for the fast lane. A gate that failed those
records would be failing the best records in the corpus.

## Decision

**A decision record is one decision, its section set is closed, its narrative
is inside a word budget, and `tools/arch-index.py --check` fails on a record
that is not.**

### 1. The section set is closed

`Context`, `Decision` and `Consequences` are required, in that order.
`Alternatives`, `Not decided`, `References` and `Measurements` are allowed.
Every other heading is retired by name, and folded into the section it belongs
to. "Alternatives in 48 of 122" read as optional-by-accident rather than
chosen, so this decides it: a record that rejected options says so under
`Alternatives`, and a record that did not does not carry the section.

Free-form numbered subsections are the case the closed set is really about.
ADR-0105's `§applied` and `§licence` and ADR-0109's amendment appendices are
the same decision accreted across a file with no index entry for any of it.

### 2. A 1500-word budget on the narrative, with the evidence exempt

The budget counts every section except `References` and `Measurements`.
Measured over the corpus: median 826 narrative words, p75 1342, p90 1724 — so
1500 binds the tail rather than the middle, which is the intent. A length gate
alone would be a bad proxy, so this one is not: the exemption is the argument.
ADR-0105 and ADR-0134 are long because they measured something, and that
measurement is why the decision holds.

### 3. An over-budget record declares why; a record inside the budget may not

A record over 1500 narrative words carries `over-budget: <reason>` in its front
matter. A record inside the budget that carries one is **rejected**, not
ignored: a parameter is either honoured or rejected by name, and an exemption
nobody needs is a field that has drifted from what it describes. An empty
`over-budget:` is rejected the same way.

### 4. The rule binds from ADR-0150 forward

`SHAPE_FROM` in `tools/arch-index.py` is this record's own number. The 135
records below it were written under no rule and are not judged by it. A policy
that arrives as a 135-file diff will not be applied; this is the same argument
ADR-0141 made about the retired metadata schema, and the same reason the retired
schema is rejected by name going forward rather than by rewriting the corpus
that carried it.

### 5. A record that refines another says so, and the other end is generated

`amends:` is the declaration, and the generated index now carries an **Amended
by** section that **derives** each record's refiners from their own `amends:`
fields rather than trusting a hand-maintained reciprocal. A record that is
retired says so in `supersedes:`, unchanged.

This is the answer to "one decision or one topic that accretes": **one
decision**. A second decision is a second record, and the second names the
first. The budget in §2 is what makes that actionable rather than aspirational —
a record that has run past the budget has usually accreted, and the finding
names the fix (`amends:`, a new number) rather than only the measurement.

### 6. The template, checked against the rule

`architecture/decisions/TEMPLATE.md` is the shape in prose, and
`ShapeTests.test_the_template_satisfies_the_rule_it_states` reads it and fails
on a heading outside the allowed set, a missing required section, a missing
front-matter field, or a template over the budget it states. A template the
gate cannot satisfy is a trap, and this is the check that says so.

### 7. One implementation, one set of findings, one gate each

`shape_findings` lives in `tools/arch-index.py` beside `corpus_findings` and
`register_findings`, and `tools/doc-freshness.py` reads it as the `adr-shape`
check rather than answering the question a second time its own way — the
arrangement ADR-0141 established and SpatialEngine-imz.2 generalised. The gate
is `arch-index.py --check`, which every lane of `eng/verify.sh` already runs
(ADR-0134 made the fast lane the merge gate; ADR-0146 is the check-not-a-test
argument for calling a check directly), so this rule costs the merge gate
nothing to run.

## Alternatives

**A hard word cap on the whole record, no exemption.** Rejected: it fails
ADR-0105 and ADR-0134, whose measurements are the argument for the decision
they record. A gate that punishes the best records in the corpus gets waived on
first use, and a waived gate is prose with extra steps.

**Leave length to judgement, gate only the section set.** Rejected: the
section set does not bound accretion, it names it. A record that accretes a
second decision does so inside `## Decision` where the section gate cannot see
it; the budget is what sees it.

**Enforce on the whole corpus at once.** Rejected for the reason in §4: a
135-file diff is not a policy, it is a merge nobody finishes, and it would
collide with the ten parallel branches currently writing records.

**Require the reciprocal `amended-by` on every `amends` link.** Rejected, and
the derivation in §5 is better: the reciprocal is a second place for the same
fact to be wrong, and 27 of the 39 links would have to be edited to satisfy it.
Deriving the other end costs the author one field and cannot drift.

**No gate; a soft budget in the template and the writing-for-agents skill.**
Rejected: a soft budget is prose, and prose is what the corpus already has. It
is in the template too — but the template is what the gate checks, not what
replaces it.

## Not decided

**Collapsing any existing record.** The bead's instruction is explicit, and
this record does not overrule it: no record below ADR-0150 is renumbered,
merged or rewritten here. The store-pushdown family is the obvious candidate
and is a separate piece of work with its own diff.

**Whether a record may carry more than one `amends:` link.** It can today, and
nothing here says otherwise. The index's Amended by section handles a record
with several.

**A budget on `References`.** Exempt, deliberately. The section is a list of
pointers, its length is not a cost to a reader who skips it, and a cap would
only ever be met by deleting a citation.

## Consequences

- A record written from ADR-0150 forward is **checkable**: three required
  sections in order, a closed vocabulary, a budget, and a stated reason when it
  is over. `tools/arch-index.py --check` fails on all four, and every lane runs
  it, so the merge gate reads the rule on every merge rather than only on a
  change that touches `tools/**` (the ADR-0143/ADR-0146 hole).
- The 135 existing records are unchanged, and the corpus does not grow a
  grandfather list: the boundary is one number. The cost is that the corpus now
  has two eras, which is the honest description of a rule adopted after the fact.
- The **Amended by** index section makes a family a lookup, and the derivation
  cannot drift from the `amends:` fields because it reads them.
- The `over-budget` field is a new front-matter key, so `tools/arch-index.py`
  accepts it and reports it when it is empty or unneeded.
- The documentation audit gains one check, `adr-shape`, read from the gate's own
  implementation, so the queue stays one list.
- The shape rule is a floor, not a ceiling. Nothing here makes a record
  *better*; it makes a bad one fail. A record that is 200 words and says
  nothing passes, and that was already true.

## References

- `tools/arch-index.py` (`SHAPE_FROM`, `REQUIRED_SECTIONS`, `ALLOWED_SECTIONS`,
  `EVIDENCE_SECTIONS`, `NARRATIVE_BUDGET`, `OVER_BUDGET_FIELD`,
  `shape_findings`, the derived *Amended by* section of `render_index`),
  `tools/test_arch_index.py` (`ShapeTests`), `tools/doc-freshness.py`
  (`adr-shape`), `architecture/decisions/TEMPLATE.md`,
  `architecture/decisions/README.md`
- `tests/architecture/Spatial.Architecture.Tests/AdrNumberingTests.cs` — what
  this record adds to, and does not replace
- ADR-0141 (the generated register and index, the one metadata schema),
  ADR-0145 (the register is generated wholesale, the summary is the routing
  prose), ADR-0134 and ADR-0139 and ADR-0143 and ADR-0146 (what runs in a
  lane, and the check-not-a-test argument), ADR-0105 and ADR-0134 (the long
  records and why they are long)
- SpatialEngine-6l6 (this record's bead, and its measurements);
  SpatialEngine-imz.1 and SpatialEngine-imz.2 (the shared-findings
  arrangement), SpatialEngine-imz.5 and SpatialEngine-imz.3 (downstream of the
  register's shape)

## Measurements

Taken in the bead's worktree over the 135 records on disk, which is the corpus
`tools/arch-index.py` read.

| Question | Measurement |
| --- | --- |
| How long is a record? | min 86 words, p25 533, median 882, mean 958, p90 1818, max 2549 |
| How long is a record's narrative, exempting `References` and `Measurements`? | median 826, p75 1342, p90 1724, p95 1879, max 2455 (ADR-0098) |
| Records over a 1500-word narrative budget | 24 of 135, which is why the budget is 1500 and not the p90 |
| Which sections does the corpus carry? | Context, Decision, Consequences in 135; `References` 55, `Alternatives` 51, `Not decided` 8, `Implementation status` 7, `Measurements` 6, `Rejected` 6, `See also` 2, `Sources` 1, and free-form numbered subsections in ADR-0105, ADR-0107, ADR-0098, ADR-0089. 36 records carry a heading outside the five the rest agree on. |
| How directional is `amends`? | 20 records declare it, 39 links in total, **27 with no reciprocal `amended-by`** |
| Does the long-record exemption matter? | Removing `References` and `Measurements` from the count changes ADR-0136 by 1068 words and ADR-0117 by 1118 — the longest records are long because they measured something |
