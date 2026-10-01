---
status: accepted
date: 2026-10-02
deciders: maintainer + agent
amends: ADR-0105
summary: The published-bundle agreement with PROJ is an **operator-run deployment exercise** — gated on a bundle in `SPATIALENGINE_GRID_DIR` and an installed PROJ, never a CI gate, and never silently absent: every run reports which bundle it covered, which it did not, and which of the two it is missing.
---

# ADR-0179: The published-bundle agreement with PROJ is an operator-run deployment exercise that reports its state

## Context

ADR-0105 §licence filed the last of its acceptance gaps rather than closing
it: a control-point suite over **published** datum shift bundles showing
sub-metre agreement with PROJ. It filed it because the suite is not a code
problem. Every published bundle is third-party data with its own terms, the
engine embeds none of them, and the decision to fetch and deploy one is an
operator's under the operator's own licence position. A gate that downloaded a
bundle would decide that question on the maintainer's behalf, and "not in CI at
all" is a legitimate answer.

What was missing was not the licence answer but the *harness*: everything the
exercise needs that is not the licence answer. ADR-0105's own tests build
synthetic `.gsb` bytes, because a test that fetched a bundle would be a test
that depended on the network; and the machine this was written on has neither
`cs2cs` nor `pyproj` installed, which is the same reason — the grid reader was
written against bytes the repository wrote itself, never against a published
file. So agreement with PROJ *over a published bundle* was not merely unrun. It
was not runnable, and there was nothing in the tree saying which of the two
things it needed.

That last part is the part that is dangerous. A suite that is silently absent
reads as agreement. ADR-0179's whole content is that the absence has to be
legible, because the one answer this exercise must never give is a green run
that means "no bundle was read by anyone".

## Decision

1. **The suite runs against `SPATIALENGINE_GRID_DIR`, and skips with a reason
   when there is nothing to run against.** The variable is a
   path-separator-delimited priority list, the same order a host's
   `Spatial:Grids:Directories` is read in (ADR-0105 §3). A skip names what is
   missing: no bundle for the datum, a directory that does not exist, a bundle
   that is present and unreadable (with the reader's own reason), or no PROJ.
   These are four different problems and an operator can only fix the one they
   are actually facing.

2. **One case always runs and always reports.** Whatever the state of the
   machine, a case prints the whole deployment — every bundle the catalogue
   publishes a row for, whether it is deployed, and where from, plus the PROJ
   state and the pinned residual — to both the trx and the console. It is the
   one thing that makes a run in which everything else skipped legible rather
   than green, and it costs a `stat` per directory.

3. **The reference is PROJ itself, run as `cs2cs -v` by EPSG pair.** Not a
   table of PROJ-generated expected values checked into the repository, and not
   pyproj. A table of them would be third-party-derived data in the tree — the
   thing §licence exists to keep out — and it would pin numbers to one PROJ
   version, so a later PROJ would fail a suite that had never actually run.
   Asking `cs2cs` means the comparison is between the engine's catalogue and
   PROJ's, live, each reading the bundle each was configured with. `cs2cs` and
   not a Python binding because the operator is already installing PROJ for its
   data files, and a second binding for the same library is a second thing to
   keep installed.

4. **A PROJ run that did not open a grid is refused, not compared.** PROJ will
   answer from a seven-parameter transformation when the grid it wanted is not
   in its data directory, and that answer agrees with the engine's Helmert
   fallback to well inside a metre. Comparing it would report agreement about a
   bundle neither side read — the exact false green this record exists to
   prevent. So the harness reads the pipeline PROJ printed, requires it to name
   a grid, and otherwise fails with the reason the operator can act on.

5. **It is not a CI gate, and a missing bundle is never a failure.** A pull
   request is not a deployment, has no licence position behind it, and would
   have to either fetch a bundle or have none — and with none, a gate can only
   be green by not running. So the cases skip, the report says so, and CI on
   `main` is not asked to cover what no deployment can.

6. **The pin is 0.1 m, an order of magnitude inside the sub-metre bar.** The
   two readers are not the same code, and a bundle's own worst-node accuracy is
   the floor neither can beat, so a pin at the sub-metre edge would be a
   measurement of the bundle's quality rather than of the reader's.

7. **The harness is pinned by tests that need neither a bundle nor PROJ.** The
   environment resolution, the four skip reasons, the report, the
   answer-reading and the grid-use refusal are all reachable from a synthetic
   deployment. The wired comparison is exercised end to end against a stand-in
   for `cs2cs` that answers whatever it is told to, in both directions:
   agreement is a zero residual and a PROJ 100 m away is a residual that fails
   the pin. A suite whose arithmetic is never exercised on the one machine that
   has neither thing is a suite nobody knows is a measurement.

8. **No bundle is vendored, and the tree says so.** A test walks the source
   directories for a file with a bundle's extension and fails on one. §licence
   was a paragraph; this is a test.

## Alternatives

- **Check in PROJ's answers for a published bundle as expected values.** It
  measures without PROJ installed, which is genuinely useful, and it is wrong
  on both counts §licence cares about: it puts third-party-derived data in the
  repository, and it freezes one PROJ version's numbers as the reference for
  every future one.
- **Fetch the bundle in the test.** ADR-0105 §licence rules it out, and
  independently it would make the gate network-dependent — and a failed fetch
  must never look like a datum error.
- **Make it a CI gate with a bundle fetched by the workflow.** It answers the
  maintainer's licence question in a YAML file, which is the one place a
  licence position should not be written down.
- **Skip silently when no bundle is deployed.** The default behaviour of a
  conditional test, and precisely the failure this record is written about: a
  line of dots is read as agreement.
- **pyproj as the reference.** Equivalent as a library, one more dependency for
  the operator to install, and it measures the Python binding rather than the
  PROJ distribution whose data directory the bundles live in.

## Not decided

**Which bundles may lawfully be deployed on a given machine, and whether any is
deployed in CI at all.** Both are the maintainer's, per bundle, and the answer
may legitimately be nowhere. This record builds the exercise and leaves the
licence position exactly where ADR-0105 §licence put it. What would settle it
is a deployment decision recorded as a deployment — a directory an operator
points `SPATIALENGINE_GRID_DIR` at — and not a line of code or configuration
this repository owns.

**The per-bundle accuracy figures a deployed bundle is published at.** Those
are read off the file (ADR-0105 §9) and are a property of the bundle, so this
record neither needs nor settles them.

## Consequences

- The last acceptance gap in ADR-0105 is now *runnable* rather than filed, and
  the tree says what it needs: a bundle in a directory, PROJ with its own copy
  of it, and nothing else. An operator has one command and one report.
- The suite is unmeasured in CI, and that is stated rather than disguised. What
  CI runs is the harness: the skip reasons, the report and the comparison
  itself, so a change to the grid path that broke the exercise would be caught
  before an operator found out.
- A repository that vendored a bundle would now fail a test rather than merely
  break a paragraph, and a wrong PROJ installation fails with a message
  pointing at the data directory instead of with a residual.
- The comparison cannot be pinned to a fixed number, so it cannot prove
  regression the way a checked-in table could. That is the cost, and it is the
  price of not putting third-party data in the tree.
- A machine that has PROJ but no bundle, or a bundle but no PROJ, reports
  exactly which of the two it is missing — which is the difference between a
  five-minute fix and a wasted afternoon.

## References

- ADR-0105 — grids are deployed, not embedded; §licence is the position this
  record builds the exercise for and §3 the priority order it reads.
- ADR-0107 — the transform verb applies the deployed grid, so the suite has
  something to compare once a bundle is there.
- ADR-0168 — NADCON alongside NTv2, which is why the North American case is in
  the suite at all.
- `tests/unit/Spatial.Transformations.ProjNet.Tests/PublishedGridDeployment.cs`
  — the harness, the control points and the report.
- `tests/unit/Spatial.Transformations.ProjNet.Tests/ProjReference.cs` — the
  `cs2cs` invocation, the answer reading and the grid-use refusal.
- `tests/unit/Spatial.Transformations.ProjNet.Tests/PublishedGridControlPointTests.cs`
  — the suite: the always-on report and the two gated comparisons.
- `eng/grid-agreement.sh` — the operator entry point.
- SpatialEngine-yt2, which closed the filed exercise's code half.

## Measurements

The state of the machine this was written on, 2026-10-02, which is the state
the suite is written to report rather than to fail on:

| Check | Result |
| --- | --- |
| `which cs2cs` / `proj` / `projinfo` | not installed |
| `python3 -c "import pyproj"` | `ModuleNotFoundError` |
| `/usr/share/proj` | absent |
| `SPATIALENGINE_GRID_DIR` | unset |

With none of the four present, the suite reports 14 passed and 2 skipped, the
two skipped carrying the reason verbatim in the trx, and the always-on report
naming both bundles, both verdicts and the missing `cs2cs`.
