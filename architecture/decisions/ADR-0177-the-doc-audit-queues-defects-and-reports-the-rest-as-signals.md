---
status: accepted
date: 2026-10-01
deciders: maintainer + agent
related: 0150
summary: The documentation audit's **queue carries defects and its report carries signals**, and the line between them is whether a reader could be *wrong*: `lint-leakage` becomes a defect class with a detectable shape — a doc that **denies** a wall `ArchitectureGuardTests` already fails on — while the restatement census it was actually measuring moves to `signals` as `lint-restatement`, and `init-fossil` moves with it, because "one commit" is a maintenance fact and its only available fix is an edit manufactured to move a counter. Neither signal is queued, so `doc-queue.md` is rows somebody can close; neither is dropped, so the census survives. No lane's verdict changes (SpatialEngine-3kk).
---

# ADR-0177: The doc audit queues defects and reports the rest as signals

## Context

`tools/doc-freshness.py` audits the documentation corpus the way the quality
loop audits code, and it is reporting-only by design (SpatialEngine-imz.2). Its
module docstring states the defect class it claims to find: a case where an
agent is not merely uninformed but *confidently wrong*, which is the only thing
worth automating (Chroma's context-rot result, arXiv 2606.15828). Everything
below is about a queue that did not measure that.

SpatialEngine-7o0 drained it from 122 findings to 10. The ten that would not
drain are two checks reporting on a green corpus.

**`lint-leakage` (6 rows)** reports any document line that restates a wall a
gate already fails on: `AGENTS.md`'s own `## Hard walls` section,
`src/Spatial.Contracts/AGENTS.md`, `README.md`, `eng/swarm-runbook.md`,
`research/rendering/README.md`, `architecture/image-service-plan.md`. Every one
is an accurate restatement, in a document whose job is to carry that wall to the
agent that must obey it — and `AGENTS.md`'s hard-walls section is required by
the repository's own instructions. Deleting the prose to clear the row would
make an agent *less* informed about the wall. The check as written was a census
of where the walls are restated, filed as a defect.

**`init-fossil` (4 rows)** reports a hand-written document with a single
commit. All four were verified accurate on 7o0's branch
(`tests/architecture/Spatial.Architecture.Tests/AGENTS.md`,
`tests/fixtures/{census,qgis}/README.md`, `.pi/skills/codebase-design/SKILL.md`),
and 7o0 declined to manufacture an edit to bump a commit counter — the churn
treadmill it had just removed from digest-staleness, which cost 99 findings of
which 2 were real.

Both shapes are the same failure: a queue row whose only fix is churn, in an
audit whose stated purpose is stopping confidently-wrong readers.

## Decision

**A finding is something a reader could be wrong about and an edit drains it; a
signal is a fact about the corpus that cannot be drained by editing a document.
`tools/doc-freshness.py` reports both, queues only the first, and its
`lint-leakage` check means the negation of a wall rather than its repetition.**

### 1. `lint-leakage` is the denial, not the restatement

Each wall in `LINT_WALLS` gains a second vocabulary beside the prose that
restates it: the prose that **denies** it. A denial is a dependency edge out of
a project the gate has already closed, or the shape ADR-0001 denies —

* a line naming `Spatial.Contracts` and any assembly
  `ArchitectureGuardTests.Contracts_source_names_no_raster_or_third_party_type`
  refuses (`Npgsql`, `NetTopologySuite`, `NetVips`, `SkiaSharp`, `ProjNET`,
  `Microsoft.Data.SqlClient`, `Microsoft.AspNetCore`), in either order;
* `Spatial.Contracts` stated as referencing, taking or depending on anything but
  `Spatial.Core`;
* `Spatial.Core` stated as depending on anything at all, or algorithms stated as
  living in it.

Those are the findings. The vocabulary that reports them is read out of the
guard test itself rather than re-guessed, and each finding names the gate that
would fail on the code the line describes.

### 2. The restatement census is a signal, and is called `lint-restatement`

It stays, because it is worth having: the count is the number of places a moved
wall would have to be revised, and a reader who finds a doc and a gate in
disagreement wants the list of every site. It is a signal because an accurate
restatement is the wall being *carried*, not a second opinion on it. On this
corpus that is six sites, permanently, which is the answer.

### 3. `init-fossil` is a signal in full

"One commit" is a maintenance fact, and it stays reported. The evidence for
keeping the check at all is that it was the right tool once: 7o0's fifth
one-commit document, a spike's baseline note, claimed the pre-pushdown store was
"the production path today" and was wrong. That defect was in the document's
*content*, which is not what the check measures — so the check earns a signal,
not a queue row, and the row it can never drain is gone.

### 4. Signals are a separate section, a separate list and a separate count

`doc-report.json` carries `signals` beside `findings`; every check entry says
which half it counts from; `doc-queue.md` renders `## Signals` under its own
heading, with the reason. `stats.findings` is what
`eng/quality-audit.sh` summarises, so the queue's headline number is the
drainable one.

### 5. No lane changes

Every check here was reporting-only and every one stays reporting-only. This
record changes what a row *means*, not whether anything fails.

## Alternatives

* **Delete the two checks.** The census is the reason `lint-leakage` exists —
it names where a wall has to be revised when the wall moves — and the one-commit
signal is the only thing pointing at an unrevised document. Dropping them
trades a question for no answer.
* **Gate `lint-leakage` on the denial only.** Tempting, and declined the way
ADR-0143/0146/0148 declined for the checks every lane calls: the denial
vocabulary is a list of prose shapes, so its false-positive rate is a judgement
this repository keeps in its records rather than in a lane's exit code. It is
ready to be promoted by a record that says so; nothing here does it.
* **Weight a signal by age** — one commit *and* no revision for N months. It
  measures elapsed time rather than correctness, and it reintroduces the same
  churn treadmill one threshold later.
* **Keep both as findings and mark them "reporting-only" in the message.** The
  current state, and the failure: the word was already on all ten rows, and the
  queue still could not be emptied.

## Not decided

What would settle it: a defect class for **superseded plans stated as current**.
That is the shape 7o0 actually found in the spike's baseline note — a document
describing a state the records have since replaced — and it is content, not
commit count. Whether it can be detected without judgement (a plan doc whose
cited record has been superseded, say) is a question for its own bead; this
record does not guess at it and no lane depends on the answer.

## Consequences

**Easier:** `doc-queue.md` is now empty, and an empty queue means the corpus has
no documented way of being wrong rather than that nobody can close a row. A new
`lint-leakage` finding is unambiguously a document telling a reader the opposite
of what the guard tests enforce.

**Harder:** a wall that *has* drifted no longer puts its six restatement sites in
front of a reader as a queue. The signal section carries them and the count is
the same six, so nothing is lost, but nothing escalates either — a wall that
moves is caught by the gate failing, not by the census.

**Forbidden:** manufacturing a revision to move a commit counter, in either
check. That is the treadmill 7o0 removed from digest-staleness, and this record
extends the removal to the last two rows it left.

**Cost:** the report grows a second list and the queue a second table, and a
consumer reading `doc-report.json` that counts `findings` alone will read a
cleaner corpus than it did — which is the point, and is why `stats.signals` sits
beside it.

## References

- `tools/doc-freshness.py` — `LINT_WALLS` (restatement and denial per gate),
  `_wall_lines`, `lint_leakage`, `wall_restatements`, `init_fossil`, `SIGNAL_CHECKS`,
  `render_queue`.
- `tools/test_doc_freshness.py` — the restatement/denial/init-fossil
  discrimination, the live-corpus state, and the queue/report agreement.
- `architecture/decisions/ADR-0150-a-record-is-one-decision-and-its-shape-is-a-gate.md`
  — the shape this record is written in, and the reason the decision was written
  down as one record rather than as a tool comment.
- ADR-0005 (third-party types never cross a contract), ADR-0001 (geometry values
  are core, algorithms are not), ADR-0141 (the register is generated and gated).
- SpatialEngine-imz.2 (the audit), SpatialEngine-7o0 (the drain), SpatialEngine-3kk
  (this decision).

## Measurements

`python3 tools/doc-freshness.py --report` over the tree at this merge
(2026-10-01), against `origin/main` immediately before it:

| state | findings | signals |
| --- | --- | --- |
| before (7o0's branch merged, 10 rows) | 10 | 0 (the two checks filed as findings) |
| after this record | 0 | 10 (4 `init-fossil`, 6 `lint-restatement`) |

The six restatement sites, unchanged: `AGENTS.md:12`,
`README.md:279`, `architecture/image-service-plan.md:91`,
`eng/swarm-runbook.md:366`, `research/rendering/README.md:25`,
`src/Spatial.Contracts/AGENTS.md:67`. The four one-commit documents, unchanged:
`tests/architecture/Spatial.Architecture.Tests/AGENTS.md`,
`tests/fixtures/{census,qgis}/README.md`,
`.pi/skills/codebase-design/SKILL.md`. Denials over 222 documents: **0**.
