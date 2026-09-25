# Scent to operation

Each row is one operation per step. Fowler's names are the vocabulary; the notes are what the move costs you in review. Grep for the target's shape, read only the matching row.

| Scent | Operation | What the step looks like |
| --- | --- | --- |
| Long method, or one method doing two jobs | Extract Method | One concern leaves as a named member; callers and tests keep the same observable result |
| Two methods changing together | Move Method, Move Field | The member moves to the type that owns the data it touches |
| A method reaching into another type more than its own | Move Method | The behaviour lands with the state it reads, and the call site stays a call site |
| Nested conditionals, deep indentation | Replace Nested Conditional with Guard Clause | Early returns replace nesting; the conditions themselves are untouched |
| The same conditional chain repeated, or switched on type | Replace Conditional with Lookup, or Replace Type Code with Subclass or Strategy | One place decides; the branches become data or types |
| Boolean flag parameters | Replace Parameter with Query, or Introduce Command | Each flag becomes a named call, and the meanings stop sharing a signature |
| Long parameter list travelling together | Introduce Parameter Object | The group becomes a type the caller already has, or a new one the tests can name |
| Data clumps travelling together | Extract Class | The clump becomes a type with its own behaviour |
| A class with several reasons to change | Extract Class | Each reason to change becomes its own type |
| Null or absent checks scattered through callers | Introduce Null Object | The absent case gets behaviour, and the checks leave the callers |
| Pass-through class with no behaviour of its own | Inline | The call site calls the real thing directly; the deletion test decides |
| Commented-out code, or comments restating the code | Delete it | The intent moves into a name; a comment earns its place by saying what the code cannot |
| Commented-out intentions ("todo: split this") | Extract the ticket, delete the comment | The note lives where work is tracked, not where it rots |
| Inheritance used for reuse across a small gap | Replace Inheritance with Composition | The relationship becomes a member, and both types can be tested alone |
| Deep inheritance, subclass overriding to extend | Collapse Hierarchy, or Move Behaviour Down or Up | The chain shortens and the override finds a home |
| One change needing the same edit in many files | Move Method or Field, then Introduce Parameter Object | The duplication is a missing module; if the edits persist, that is `codebase-design`'s question, not a move's |
| Creation of a type scattered through callers | Extract Factory | The construction rule, and its rules for variants, live in one place |

## Choosing

- Smallest row that removes the scent. Extract Method answers most long methods; a collapse or a composition swap is a last resort, not a first move.
- One row per step, even when two rows look like one job. The second row is the next step, and it gets its own gate run.
- Scent on a public contract, a wire payload, or a `CancellationToken` path: read [hazards.md](hazards.md) before the edit. Those moves change more than shape.

## When the rows run out

A target that resists every row, or a change that keeps wanting the same edit in several places, is a shape problem: the module is shallow, or the seam is in the wrong spot. Take the deletion test and hand the question to `.pi/skills/codebase-design`.
