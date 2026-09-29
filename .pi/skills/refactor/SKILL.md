---
name: refactor
description: Behaviour-preserving simplification, one small step at a time on a green safety net. Use when the work is reshaping code that already exists: refactor, simplify, clean up, tidy, de-duplicate, extract, inline, rename, split, restructure, or fix a named code smell (long method, god class, deep nesting, feature envy, duplicated logic, long parameter list). New behaviour and bug fixes are feature work, not this skill.
---

# Refactor

**Behaviour-preserving**: every step leaves what the program does externally identical. A change that alters behaviour is a feature change and needs the user's explicit go-ahead. The gate is the whole safety net: at every moment you have a way back.

## 1. Raise the safety net

Run the repo's documented gate before touching anything (`eng/verify.sh --full` here — the bare `eng/verify.sh` is the scoped build gate, ADR-0118; elsewhere the documented build-and-test command). A gate that cannot go green is a finding, not a licence: report it, and offer characterisation tests (see [references/hazards.md](references/hazards.md)) as the way to build one.

*Done when:* the gate is green on the tree as it stands, and the exact command and result are written down. Step 5 compares against that line.

## 2. Name the target and the scent

One target, in one sentence: the file, the member, and the observable behaviour that must survive the change. Take the user's target when they name one. Otherwise start where the repo already points: the ranked offenders in `crap-queue.md` and `metrics-queue.md`, and the churn in recent history.

Name the **scent** in Fowler's word for it. [references/moves.md](references/moves.md) maps each scent to the operation that removes it; grep that table for the target's shape rather than reading the file whole.

*Done when:* the target is written as file plus member plus behaviour-to-preserve, and the scent is named.

## 3. Choose one operation

Pick the smallest operation from [references/moves.md](references/moves.md) that removes the scent. Two operations are two steps, always.

*Done when:* the operation has a name, and the line range it will touch.

## 4. Take one small step

Apply that operation and nothing else. A drive-by tidy in the same hunk is a second operation hiding in the diff, where the reviewer cannot see which change did what. Public surfaces, exception codes, logging, and diagnostics come through the step unchanged.

*Done when:* the diff for this step is the one operation.

## 5. Re-run the net

Same command as step 1.

- **Green**: the step stands. Look for the next scent in the same target, or report.
- **Red**: revert this step whole (`git restore` the paths), then diagnose before touching anything again. Either the operation was wrong for this code, or the net does not cover the behaviour you moved. Repairing forward on a red net is a behaviour change wearing a refactor's clothes.

For steps 4 and 5, run the narrowest gate that would catch a break in the target, usually the test project covering it. The full gate is the report's gate.

*Done when:* green, or reverted with the reason written in one line.

## Stop conditions

- **Two reversions on the same target**: the operation is not the problem, the module's shape is. That is a design question; `.pi/skills/codebase-design` carries the vocabulary (depth, seam, deletion test).
- **The step needs a signature change, a new config key, a new dependency, or a data migration**: name it as its own piece of work and let the user choose. A change that reverting a hunk cannot undo has stopped being a refactor.
- **The behaviour you are about to move has no test**: say "untested" in the report. A refactor there is a bet, and a named bet is a decision the user can take.
- **The step crosses a hard wall in `AGENTS.md`**: stop and name the wall. Those are decided.

## Hazards

[references/hazards.md](references/hazards.md) lists the moves that only look behaviour-preserving: wire-visible names, culture and parsing, double arithmetic, cancellation and timing, exceptions across a `try` boundary, lock scope, cached time and randomness, generated and pinned files, and fakes that reimplemented the logic being moved. Consult it before the first step into an unfamiliar area.

## Report

Close with: the operations applied in order, the gate result for each, every reversion and its reason, and what you deliberately left alone.
