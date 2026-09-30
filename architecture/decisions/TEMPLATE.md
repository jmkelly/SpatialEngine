---
status: proposed
date: YYYY-MM-DD
deciders: who decided
summary: The decision in one line, which is what the register renders and what an agent reads to decide whether this record is relevant. Say what was chosen, not what the situation was.
# The four cross-references, all optional, all one line of comma-separated
# numbers. Delete the ones this record does not need; an empty one is noise.
# amends: ADR-NNNN              this record refines that one, and is one of several
# amended-by: ADR-NNNN           that record is one of the several that refine this one
# supersedes: ADR-NNNN           this record retires that one
# superseded-by: ADR-NNNN        this record is retired by that one
# related: ADR-NNNN              read alongside, neither amends nor supersedes
# consulted: ADR-NNNN             read while deciding, and not a cross-reference
#                                  this record asserts
# over-budget: <why>             only on a record over 1500 narrative words
---

# ADR-NNNN: the decision, in the imperative

<!--
  The template for a decision record. Copy it, reserve the number first
  (`python3 tools/adr-next-number.py --reserve --bead <id>`), and write the
  record under the number you were given.

  The shape below is the shape `tools/arch-index.py --check` enforces on every
  record from ADR-0150 forward, and this file is checked against it too, so a
  template the gate cannot satisfy is not a template:

    python3 -m unittest tools.test_arch_index.ShapeTests

  Four rules, all of them gates:

    * the section set is closed. Context, Decision and Consequences are
      required, in that order. Alternatives, Not decided, References and
      Measurements are allowed. Anything else is retired by name — fold it
      into the section it belongs to.
    * the narrative is inside 1500 words. References and Measurements are
      exempt, because a record is long when it carries measurements and those
      may be load-bearing (ADR-0105, ADR-0134).
    * a record that is over the budget with one decision to say declares
      `over-budget: <why one record cannot be shorter>` in the front matter.
      A declaration on a record inside the budget is rejected, not ignored.
    * a record that refines an earlier one says so in `amends:`, and a record
      that is retired says so in `supersedes:`. Both are the index's only
      reading of a family, so an undeclared amendment is invisible.

  Two questions worth answering before you start:

    * is this one decision? If it is two, it is two records, and the second
      carries `amends:` naming the first.
    * what did you reject? A record that does not say what it did not choose
      cannot be reviewed by anyone, including you in six months.
-->

## Context

What was true before this record, and why the question arose. The constraint,
the failure, or the measurement that made the question worth a number. Not the
history of the feature — the state that made a decision necessary.

## Decision

**What was chosen**, in the imperative, in the first sentence. Then the parts
that need saying: the boundary, the exception, the thing that is explicitly not
in scope. A reader who stops after this section should be able to implement the
decision or explain why not.

## Alternatives

What else was considered, and why it lost. One entry per option, with the
reason — "it does not work", "it is more than we need", "we have no way to
measure it". An option rejected for no stated reason is a decision nobody can
revisit, because the reason is the part that goes stale.

## Not decided

What this record deliberately leaves open, and what would settle it. Optional,
and worth more than it looks: a boundary an agent has to guess at is one it
will guess wrong.

## Consequences

What follows from the decision, in both directions: what becomes easier, what
becomes harder, what is now forbidden that was permitted. Include the cost. A
record whose Consequences section has no cost in it has not been argued with.

## References

The records this one reads, the files it changed, and the beads that decided
it. This section is exempt from the word budget, and so is the one below it.

## Measurements

What was measured, how, and what the numbers were — a table, with the tool and
the date. Exempt from the budget, because a record is often long precisely
because it measured something, and that measurement may be the load-bearing
part of the decision (ADR-0105, ADR-0134).
