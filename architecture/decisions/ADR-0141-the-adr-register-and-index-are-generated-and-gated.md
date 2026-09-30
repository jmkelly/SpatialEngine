---
status: accepted
date: 2026-09-30
deciders: maintainer + agent
summary: The ADR register, the ADR index and the ADR metadata are generated and gated on every verification lane, so a decision record cannot drift from the routing that points at it
amends: ADR-0109, ADR-0118
amended-by: ADR-0145
---

# ADR-0141: The ADR register, the ADR index and the ADR metadata are generated, and every lane gates them

## Context

`architecture/decisions/` held 126 decision records on 2026-09-30 and the
register table in `architecture/distilled/README.md` listed 93 of them. The 33
absent rows were not the old ones nobody cared about: ADR-0097 (a pushdown is
allowed only where it is identity-preserving, and a literal binds as its
column's kind) and ADR-0098 (the store query surface — projection, order,
paging, count, distinct, aggregate) are the two records that rewrote the
feature-query contract ADR-0074's row still described in its original shape,
and ADR-0107 — the newest decision in the tree — was invisible. An agent
routing by task lands on ADR-0074, reads one line, and is confidently wrong
about the surface it is about to change. Nothing prunes a hand-edited table,
and the register is the only thing standing between an agent and the records.

The records themselves were in two metadata schemas: 93 with YAML front
matter, 33 stating their standing as a `Status:` line (or, in ADR-0030's case,
a `- **Status:**` bullet) after the H1. That is a single-source-of-truth
violation with a measurable cost: `architecture/distilled/README.md` said
ADR-0002 was superseded by ADR-0033 while ADR-0002's own file said
`Status: Accepted` and said nothing about being superseded at all. "Is this
decision still live?" was a grep through prose, and a third of the corpus
would not answer it.

Two smaller faults rode along. The digests kept the superseded groups by hand
(the four records ADR-0039 supersedes, the fifteen ADR-0033 supersedes), which
is the same drift with a shorter fuse. And a citation of an ADR that does not
exist was not a finding: nothing read `ADR-NNNN` anywhere, so a rename or a
delete left prose pointing at nothing and no gate noticed.

The lanes already have the shape this needs. ADR-0109 implemented three lanes
and ADR-0134 made the fast lane the merge gate; both are about *code*. This
record is about the corpus those lanes are pointed at by.

## Decision

**The register and the index are generated from the records, the records carry
one metadata schema, and every lane of `eng/verify.sh` fails when what is
committed is not what the records generate.**

### 1. One schema: YAML front matter

Every `architecture/decisions/ADR-NNNN-*.md` carries front matter with
`status`, `date`, `deciders` and `summary`, and optionally `supersedes`,
`superseded-by`, `amends`, `amended-by`, `related` and `consulted`. An unknown
key, a missing required key, a date that is not `YYYY-MM-DD`, a `Status:` line
or a `- **Status:**` bullet after the H1, and an H1 whose number is not the
file's, are each a finding by name. The retired schema is rejected rather than
tolerated, because a record that states its standing two ways states it wrongly
in one of them.

`summary` is the new required field, and it is what makes the register
generatable: one line, the decision, written in the record. The 33 records that
had no register row got one authored from their own decision text, and the
rows that had run to a paragraph were condensed — the record is where the
detail lives, and the register is a routing table, not a second copy.

A record that is superseded now says so in its own front matter
(`status: superseded`, `superseded-by: ADR-0033`), so `superseded-by` is
derived rather than asserted a second time in a digest.

### 2. Three generated artefacts, one read

`tools/arch-index.py` reads `architecture/decisions/*.md` once and writes:

* the **register table** in `architecture/distilled/README.md`, between
  `<!-- arch-index:register:begin -->` and `<!-- arch-index:register:end -->`
  — one row per record on disk, strikethrough and "superseded by" for the
  ones that are;
* **`architecture/decisions/README.md`** — every record with its title,
  status, date and cross-references, then the superseded groups **by
  superseder** (which is the hand-maintained list the digests carried) and the
  burned numbers;
* **`arch-index.md`** at the repository root — a ten-line breadcrumb, so
  routing is pointer-first: `AGENTS.md` → `architecture/distilled/README.md` →
  `architecture/decisions/README.md`.

`--write` regenerates; `--check` is the gate; `--citations` is the citation
read on its own, because it is the cheapest check in the repository and the
documentation-freshness audit (SpatialEngine-imz.2) wants it without the rest.
`--root DIR` reads another tree, which is how the tests exercise a deleted, a
renamed and a rewritten record without touching this one.

### 3. Burned numbers are recorded, not inferred

0093–0096, 0099 and 0102–0104 are spent: a number was taken and given back, or
burned by work that never landed. They are listed as burned in the generated
index, and a file appearing at one of them is an error. **Every other gap is
not inferred to be burned**: `tools/adr-next-number.py --list` shows numbers
held by open reservations on parallel branches, and those are in flight rather
than retired. The list is a constant in the tool with that reasoning attached,
not something derived from "the file is not there".

### 4. Every lane runs it, before anything else

`eng/verify.sh` runs `python3 tools/arch-index.py --check` on every lane —
default, `--format` and `--full` — and under `--plan`, which prints it and runs
nothing. It is a second or two, and it is not scoped: the corpus is the
repository's, not the change set's, so scoping it by `git diff` would skip the
check exactly when a branch is deepest in decisions. It runs first, before the
format lane's `exit 0`, because a stale register is cheaper to find than a
formatting violation is to fix afterwards.

The gate fails on a deleted or renamed ADR (the register and the index no
longer match), a register row that no longer matches its file, a record in the
retired schema, a record with two numbers or a burned number with a record, a
missing generated file, and a dangling `ADR-NNNN` citation in any `*.md` or
`*.cs` file. The citation read walks the prose an agent reads and the code an
agent greps, and knows the burned numbers, so the index's own list does not
trip it.

The citation rule is not new — `AdrNumberingTests.Every_adr_reference_resolves_to_exactly_one_record`
in the structural guard already reads every `ADR-NNNN` in the tree, and
`Distilled_adr_register_lists_each_number_once` reads the register's rows. This
one exists beside it for two reasons and claims nothing more: it runs in a
second *before* the build rather than after it, so a stale index is the message
an agent reads first rather than a red xUnit line; and it is callable on its
own (`--citations`), which is what the documentation-freshness audit
(SpatialEngine-imz.2) and the per-package `AGENTS.md` derivation
(SpatialEngine-imz.5) need. The two rules agree by construction on this tree:
a number is citable when a record carries it, and the burned numbers are
written into the index as **bare** numbers precisely so the guard does not
read the one line that names numbers without records.

## Consequences

- An agent routing by task reads a register that is complete by construction,
  and follows a record's `superseded-by` to the decision that replaced it
  rather than to a row in a table somebody remembered to update.
- Writing an ADR now means: reserve the number (ADR-0089), write the record
  with front matter, and run `python3 tools/arch-index.py --write`. The gate
  says so by name if it is skipped.
- A register row can no longer disagree with its record, because there is only
  one of it. The cost is that the digest's prose around the block is still
  hand-written and can still be wrong; the gate checks the block.
- The corpus grew by one schema and one tool. `tools/test_arch_index.py` is the
  reproduction: it fails on a deleted record, a renamed record, a `Status:` line
  and a dangling citation, and it asserts that a no-op `--write` is
  byte-identical.
- The check is `--citations`-callable, so the documentation-freshness audit
  (SpatialEngine-imz.2) and the per-package `AGENTS.md` derivation
  (SpatialEngine-imz.5) read the same corpus through the same tool rather than
  each writing its own reader.
- `Spatial.Architecture.Tests` reads the same two invariants in C# — a number
  is claimed once, a citation resolves — and still runs on every lane. The
  python read is earlier, cheaper and callable on its own; the C# one is the
  structural guard and is not replaced by a script the guard's own build
  depends on. Neither was changed.
