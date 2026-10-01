---
status: accepted
date: 2026-09-28
deciders: maintainer + agent
summary: Datum shift grids are deployed, not embedded: the registry reads NTv2 bundles and the classic Helmert stays the stated fallback.
amended-by: ADR-0107, ADR-0168, ADR-0179, ADR-0180
---

# ADR-0105: Datum shift grids are deployed, not embedded, and the classic Helmert stays the stated fallback

## Context

ADR-0027 catalogued OSGB 36 on the classic seven-parameter Helmert and said
plainly why: the WKT1 library carried no grid, so the catalogue's accuracy for
Ordnance Survey is metres rather than centimetres. ADR-0087 then made datum
transformations values and made `findTransformations` a search over them,
ranking candidates by stated accuracy — and named grid shifts as its follow-on,
"a third candidate kind with its own area of use and accuracy", to be slotted
into the graph as a first-class operation type. This ADR is that follow-on.

The depth is real. The Helmert is a seven-parameter approximation of a shift
that varies across the ground; over Britain it is good to about 0.1 m in the
middle and wrong by metres at the edges, and the error grows for every other
non-WGS 84 national datum. A client doing survey-grade work is told a number
the engine cannot deliver.

Three things make this more than a file reader.

**Licence and redistribution.** Every published grid is third-party data with
its own terms. The Ordnance Survey's OSTN15 is Crown copyright under an Open
Government Licence; NADCON and the NAD83 realizations are US Federal Government
works with their own conditions; the Canadian, Australian and New Zealand grids
are national mapping agency products. None of them is ours to vendor, and
ADR-0049's precedent — the engine embeds Noto Sans because *we* chose a font we
could embed — does not transfer. ADR-0049 embedded an asset whose licence
permits exactly that and recorded the version and hash. A grid bundle is
materially different: it is large, it is derived from someone else's
observations, and its terms are about redistribution rather than use. So the
ADR-0049 question ("embed, fetch, or configure a path?") has a different answer
here, and the reason is the licence rather than the size.

**Where the data comes from at test time.** Whichever way the licence lands, a
test suite cannot depend on fetching a bundle: that makes the gate
network-dependent, and a failed fetch must never look like a datum error.

**What "fall back" means.** The engine is a general-purpose Geometry Service
serving a delivered workbench. A grid that is not deployed, and a point outside
a grid's block, are both ordinary states. The engine has to answer anyway, and
it has to say what the answer cost — a coordinate placed to 3 m by an
approximation and returned without that fact is the failure mode ADR-0087 was
written against.

## Decision

1. **NTv2 (`.gsb`) is the format read; NADCON is not, in this slice.** The
   registry reads the NTv2 bundle format: an eleven-record overview header, then
   per sub-grid a header and a block of four single-precision floats per node
   (latitude shift, longitude shift, and the two accuracies). Header records are
   looked up by key rather than counted into position, blocks stored
   north-to-south or west-to-east are normalised to south-first, and shifts are
   stored in seconds of arc and added in degrees. NADCON (`.las`/`.los`) is
   deliberately not implemented: it is a different container with different
   semantics, and a binary parser written without a bundle to test it against is
   a parser nobody can check. It is filed as follow-up work rather than shipped
   unread. The format is published with each operation, so a client is told which
   standard its shift came from.

2. **Grids are configured, not embedded and not fetched.** A host names one or
   more directories — `Spatial:Grids:Directories` — and a grid is deployed by
   dropping a bundle into one. The registry is the whole of the deployment
   surface. This is the ADR-0049 question answered the other way, and the
   licence is the reason: the licence stays with whoever holds it, the engine
   carries no third-party redistribution obligations, and no artefact of ours
   grows by the size of a national grid. The corollary is that the engine is
   identical whether or not a bundle is present, so "no grid" is an ordinary
   state and never a start-up failure.

3. **The directories are a priority order, not a set.** The first directory
   holding a bundle for an operation wins, so an operator can shadow a shipped
   default without editing it. A directory that does not exist is remembered and
   echoed back rather than silently dropped, because an operator who mistyped a
   path needs to see it. A bundle that is present and unreadable is recorded as
   a failure with its reason, and the search continues to the next directory: a
   broken file in a shadow directory must not take the host down.

4. **A bundle is read on first use and then cached; only a found grid is
   cached.** Reading is lazy so that a bundle deployed under a running host is
   picked up without a restart, and caching the miss would defeat exactly that.
   A bundle that *was* found is served from memory thereafter, because a grid
   runs to megabytes and a transform must not re-read and re-parse one per
   request. The trade is explicit: **redeploying a grid is a restart.**

5. **A grid is a value, published as a table, and its coverage is part of it.**
   `CrsTransformationStep` gains a `GridShiftParameters` alternative to
   `HelmertParameters` — the grid's sub-grid name, file name, format,
   interpolation, and the block of ground it covers. Exactly one of the two is
   set. A grid shift is not a seven-parameter transformation in any sense a
   client could apply, so inventing a zero Helmert to stand in for one would be
   a decoration a client could mistake for an operation. The file is named but
   not its path, so publishing an operation does not publish the host's disk
   layout. `Parameters` is nullable for the same reason, and the existing
   consumers were changed to assert which kind of step they hold rather than
   null-forgiving the read.

6. **The out-of-grid case falls back to the classic Helmert, and says so.** The
   alternative — refusing a coordinate the engine can place to 0.1 m because no
   bundle was deployed — would be a regression for the delivered workbench, and
   quietly answering as though the grid had been used would be worse than
   either. So the Helmert stands, and the *response states the fallback*:
   the grid candidate's method text names the bundle, the interpolation and the
   block it covers; the Helmert candidate's method text says whether a grid is
   deployed and ranked ahead of it, and what the Helmert is stated at. The Esri
   listing has no field for any of this, so it is carried in the method string
   for the same reason ADR-0087 put the Helmert approximation note there.

7. **A grid is only valid inside its own block, and a point on its outer edge
   is outside it.** A grid is tabulated over a block; beyond it there is no cell
   to interpolate from, so a point there is outside the grid rather than on it.
   The engine never extrapolates across that edge. A point is interpolated
   bilinearly from the four nodes around it, at its own fractional position in
   the cell, so a point exactly on a node gets that node's own shift. The
   inverse is iterated to a fixed point, because a tabulated shift is not
   analytically invertible and a forward and inverse that disagreed would report
   a round trip that did not close.

8. **A grid-backed candidate is composed, not special-cased.** It is the
   ADR-0087 path — the source datum to the WGS 84 pivot, then the pivot to the
   target — with the grid standing in for the Helmert leg of whichever datum it
   serves. So a pair whose datums are both grid-backed uses both, concatenated
   through the pivot, exactly as two Helmerts are today. One difference from
   ADR-0087: a grid-backed operation's area of use is the **intersection** of
   its legs' areas, not the union. A Helmert concatenation is valid wherever
   either step applies because both steps are globally defined; a grid leg is
   only defined inside its block, so that block bounds the whole operation
   whatever the other leg covers. An area of interest off the block therefore
   filters the grid candidate out and leaves the Helmert, which is the correct
   answer rather than a lost one.

9. **Accuracies are derived, and a grid's is its worst node.** The grid
   operation rows carry no accuracy and no extent: both are read off the file,
   because a grid is only as good as its worst node and only applies over its
   own block, and a number typed out beside the file name would be a second
   claim about the same operation that could disagree with it. The published
   accuracy is that worst-node accuracy combined in quadrature with the other
   leg's registered accuracy — the ADR-0087 rule, unchanged. A datum that is
   the pivot contributes no leg and no error, which is what makes a grid against
   WGS 84 exactly as accurate as the grid says it is. A grid candidate is
   `Approximate: false` when no Helmert leg survives, and `true` when one does: a
   candidate is only as exact as its least exact leg.

10. **The grid operations are their own table, keyed the way the graph is.**
    `EpsgGridShiftOperations` sits beside `EpsgDatumOperations`, not inside it,
    because the two answer different questions — a grid operation is a different
    operation, and under ADR-0086 a different kind of row, not a new column on
    the Helmert one. The join is on the same `GraphName` token the operation
    table already publishes, so a graph node needs no second copy of the
    catalogue's datum codes and the two tables cannot drift on codes.

## §applied: what the engine does with a deployed grid, and what it does not

> **Amended by ADR-0107.** The transform verb now applies a deployed grid, per
> coordinate, over the classic Helmert. Every claim below about the verb still
> performing the Helmert is superseded; the measurements stay, because they are
> what the fix was built on. What is *not* superseded is the licence position
> below and the last paragraph: the grid is still deployed rather than vendored,
> and agreement with a published bundle is still unmeasured.

**This slice publishes the grid operation; it does not yet apply it.**
`project` and the coordinate transform path still perform the classic Helmert,
byte for byte as before, and the grid candidate's method text says so.

That is a real gap and it is stated rather than papered over. The alternative was
to intercept the transform path and re-compose it as
*source CRS → source datum's geographic coordinates → datum shift → pivot →
target*, and it was measured and rejected rather than assumed. ProjNet applies a
datum's shift wherever a pair of coordinate systems disagree about a datum, not
only in the source-to-target direction the engine uses today, so every leg of that
re-composition turned out to re-apply a shift that had already been applied: a
same-datum "projection only" leg was out by 113 m at London, and the
geographic-to-geographic legs were not mutual inverses. Making the re-composition
correct needs shift-free copies of the source and target systems, and then a
hand-rolled Helmert for the fallback leg — duplicating geodesy the engine already
delegates to ProjNet, and replacing the one path the existing control-point tests
hold to 0.1 m against PROJ, in a change with no PROJ available to check it
against.

So the invariant ADR-0087 §6 states — the first candidate is the path the engine
applies — is knowingly broken for the grid candidate, and the listing says so in
every grid method string. It is the one way this could mislead a client, which
is why it is a method string rather than a note in this file. **Applying the
grid in the transform verb is the first item of the follow-up**, and it should be
done as shift-free systems plus a decision about the Helmert, with a PROJ
reference to check it against — not as a re-composition of ProjNet's own.

## §licence

No grid bundle is vendored, embedded, or downloaded by the engine, the build or
the tests. The tests build their own `.gsb` bytes (a synthetic constant-shift
grid), so no published grid is redistributed and no test depends on the network.
A deployment is an operator decision made under the operator's own licence
position; the engine's obligation is to publish which file and which sub-grid
served an operation, which it does, so an operator can account for what was
deployed.

The follow-up that would close the last acceptance gap — a control-point suite
over *published* bundles showing sub-metre agreement with PROJ — is a deployment
and licence exercise, not a code one. It is filed as such.

## Consequences

- A host with a bundle configured publishes grid-backed transformations with
  real parameters, real coverage and a real accuracy, and a host without one is
  exactly the host it was before.
- Nothing is redistributed on anyone's behalf, and the licence question has one
  answer rather than a per-bundle negotiation baked into a vendored artefact.
- The reader refuses anything it cannot fully account for. A bundle whose node
  block is short, whose increments do not divide its block, or whose header is
  not a header yields a reason and no grid. A datum shift is exactly the thing
  that must not be half-read: a grid that silently covers less than its header
  claims puts a datum error into every coordinate that crosses the gap.
- The contract grows a nullable `Parameters` and a `GridShift` alternative, so
  every consumer of a transformation step must now say which kind of step it
  holds. That is a real cost and it is the right one: the alternative was a step
  carrying both, one of them always meaningless.
- A grid redeploy needs a restart. Stated here rather than discovered later.
- NADCON is not supported, and the method text says which format an operation
  used, so a client is never left guessing.
- The engine and the delivered clients are otherwise unchanged: the search is
  served on the Esri surface where clients already ask, and the TypeScript SDK
  does not model `findTransformations` at all (ADR-0087), so there is no SDK
  change to make.
