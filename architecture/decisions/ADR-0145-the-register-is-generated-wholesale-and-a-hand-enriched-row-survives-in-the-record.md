---
status: accepted
date: 2026-10-01
deciders: maintainer + agent
summary: The ADR register is **generated wholesale** and never hand-edited (ADR-0141), so a hand-enriched row survives generation by moving its prose into that record's own `summary` — the field the generator renders — rather than by a merge policy that would make a row's wording depend on who touched the file last. A rebase-and-regenerate that shortens an existing row is a proposal to delete a routing clause and is reviewed as a change to the record that carries it; the conflict shape is mechanical (take the row each side has, regenerate), and `tools/test_arch_index.py` pins the clauses `main` had hand-enriched so a shortening fails by name (amends 0141).
amends: ADR-0141
---

# ADR-0145: The register is generated wholesale, and a hand-enriched row survives in the record

## Context

ADR-0141 made the ADR register in `architecture/distilled/README.md` a
generated block, and gave every record a `summary` in its front matter for
the generator to render. On 2026-10-01 the branch carrying ADR-0141
(`bd/SpatialEngine-imz.1`, `b658db8`) could not be rebased onto
`origin/main`: both sides had edited the register table, so the conflict was
not a textual merge.

The asymmetry is what made it a decision. The branch's generator shortens
rows — its table is what a `summary` written to be one line looks like.
`origin/main`'s table, meanwhile, has been hand-enriched row by row since the
branch was cut: ADR-0134 carries the measured scoping numbers and the two holes
the fast lane closed, ADR-0122 carries the cache TTL's configuration name,
ADR-0132 states why the fold is over the ASCII alphabet, ADR-0142 carries the
recorded corpus. An agent routing by task reads those clauses to decide
whether a record is relevant; they are the register's job, and the register is
the only thing standing between an agent and 140 records.

So the three shapes on the table were real: accept the generator's shorter
wording and lose the prose; let the generator preserve whatever wording the
digest happens to carry and fill in only what is missing; or move the prose
into the records and render it from there.

## Decision

**The register is generated wholesale — there is no hand-edited register — and
the prose a hand-edited row carried is preserved by moving it into that
record's own `summary`, which the generator then renders.**

### 1. Wholesale generation, no merge policy

The block between the `arch-index:register` markers is the generator's, all
of it. The alternative that keeps the digest's wording is rejected: it makes
the byte-identical no-op property of ADR-0141 a property of a merge policy
rather than of the files, so a row's wording becomes a function of who touched
the file last, and the gate stops being able to say what the register says.
A register that is "the last writer's wording, plus the generator's missing
rows" is the drift ADR-0141 exists to remove, one merge later.

### 2. The record is the place the prose lives

When a row in the digest is richer than the record's `summary`, the fix is to
write the richness into the `summary`. The summary is a required field of the
one metadata schema ADR-0141 introduced, it is read by the generator, and it
is in the file that owns the decision — so the row cannot disagree with the
record, and the prose follows the record rather than the table. Nothing is
lost in the move: the text is the same text, one level deeper, and
`architecture/decisions/README.md` renders it beside the record's status and
cross-references for an agent that reads either surface.

A `summary` is one line of Markdown and may run to a paragraph (ADR-0134's is
the longest at ~1.5 kB). It is a routing line, not a synopsis: it states what
was decided, with the clause that makes the decision checkable, and it points
at the record for the rest.

### 3. A row that would shrink is a change, and it is reviewed as one

A rebase, a re-run, or a rebase-and-regenerate that makes an existing row
shorter is not a mechanical consequence of running the tool; it is a proposal
to delete a routing clause, and it is reviewed as a change to the record that
carries it. The reproduction for it is in `tools/test_arch_index.py`
(`test_register_keeps_the_hand_enriched_routing_clauses`): it pins clauses
from the rows that were hand-enriched on `main`, so a generator that shortens
them fails by name instead of passing a review nobody was looking for.

### 4. What a future conflict of this shape does

The rule is mechanical, which is the point: when a branch that regenerates the
register meets a `main` that has edited it, take `main`'s row text as the
record's `summary` for every row both sides have, take the branch's for the
rows only it has, and regenerate. No prose is dropped and no policy question
is reopened. SpatialEngine-fj2's merge was the degenerate case of this (both
sides only added a row); this record is the general one.

Two details are part of the rule rather than of that one merge, because both
lose a clause otherwise. The generator writes a superseded row as
`~~<summary>~~ — superseded by NNNN`, so a qualifier `main` hung off the
superseder ("superseded by 0033 (in-process default)") moves **inside** the
struck text, where the standing clause and the reason for it are struck
together; and a clause that is itself generator decoration ("its merge-gate
clause is superseded by **0134**") is dropped, because the generator re-derives
it from the record's own `superseded-by`.

One row yields to the record rather than to `main`: ADR-0030's digest row read
"~~Host API capability envelopes~~", a label from a decision the record no
longer describes, while the record's `summary` and its H1 agree on what ADR-0030
decided. The register is rendered *from the records*, so where a digest row and
a record disagree in substance rather than in detail, the record's own wording
is what the row says. That is the whole reason the prose lives there.

## Consequences

- `bd/SpatialEngine-imz.1` rebases onto this record with the digest's rows
  preserved verbatim as the records' `summary` fields, and its regeneration
  adds the rows `main` never had without touching the rows it did.
- The register is a pure function of `architecture/decisions/*.md` again, so
  ADR-0141's byte-identical no-op holds against the files alone.
- Writing a record with a thin `summary` while the digest's row is rich is
  still possible, and the gate will not catch it — the gate compares the block
  with the records, not a row with an earlier version of itself. The
  reproduction above is a test, not a gate, for that reason; making it a gate
  would need a stored previous revision, which is a history problem this
  repository does not otherwise have.
- An agent editing a register row by hand now edits a record instead, which is
  a larger edit and a more explicit one: the number is reserved, the standing
  is stated, and the text lands where a reader of the record will find it.
