---
status: accepted
date: 2026-09-29
deciders: maintainer + agent
summary: The transform verb applies a deployed NTv2 grid per coordinate — source geographic → grid → WGS 84 → target — over the classic Helmert (amends 0105).
amends: ADR-0105
---

# ADR-0107: The transform verb applies a deployed datum shift grid per coordinate, over the classic Helmert

## Context

ADR-0105 made datum shift grids a deployed, configured thing the graph
composes and the catalogue publishes, and left one gap on purpose. It
published the grid operation; it did not apply it. `project` and the
coordinate transform path still performed the classic Helmert byte for byte,
and every grid method string said so, because ADR-0087 §6 promises that the
first candidate is the path the engine applies and a grid is ranked first on
accuracy. That promise was knowingly broken for the grid, and the broken
promise was priced into the method text rather than papered over.

ADR-0105 §applied also recorded *why* it was not simply re-composed, and the
measurements are the reason this ADR exists in the shape it does. ProjNet
applies a datum's shift wherever a pair of coordinate systems disagree about a
datum, not only in the source-to-target direction. So the obvious composition —
*source CRS → the source's own geographic coordinates → datum shift → WGS 84 →
the target's own geographic coordinates → target* — re-applies a shift at every
leg:

- a "projection only" leg taken between the catalogued OSGB systems
  (`CreateFromCoordinateSystems(osgbGeographic, osgbProjected)`) lands 529930.27
  at London where the direct 4326→27700 lands 530043.19, out by 113 m;
- the geographic-to-geographic legs are not mutual inverses:
  `CreateFromCoordinateSystems(4326, 4277)` gives −0.125994 and
  `CreateFromCoordinateSystems(4277, 4326)` gives −0.129206 for the same London
  point, ~222 m apart.

Two of the three things ADR-0105 said a correct fix would need are therefore
settled by measurement rather than by choice:

1. **Shift-free copies of the source and target systems** are not optional.
   They are what makes an outer leg pure projection maths, so no shift exists
   there for ProjNet to re-apply.
2. **The Helmert fallback leg needs neither of the two options ADR-0105
   offered.** It does not need a hand-rolled seven-parameter Helmert, and it
   does not need a ProjNet-side fix. Pairing a *catalogued* system with a
   *shift-free* copy of the other end is already the pairing ProjNet evaluates
   as one datum shift, in the direction asked for — one end carries the
   conversion, the other carries the same datum with the conversion zeroed, so
   there is nothing for ProjNet to add. The fallback is ProjNet's own maths,
   asked in the direction the engine means.

The remaining requirement is the one ADR-0105 named: the choice is per
*coordinate*, not per request, because a grid is a fact about the ground and a
geometry is not obliged to stay inside one block.

The fourth requirement — a PROJ reference to check the result against — is
**not met on the machine this was written on, which has no PROJ binary**. That
is stated in §measured rather than assumed, and the consequences of it are
named in §not measured.

## Decision

1. **A plan, not a re-composition of ProjNet's own.** Where a deployed grid
   serves either datum of a CRS pair, `Spatial.Transformations.ProjNet` builds a
   `GridShiftPlan`: *source → the source's own geographic coordinates → datum
   shift → WGS 84 → the target's own geographic coordinates → target*. The
   datum shift is the engine's, and the two outer legs are pure projection
   maths. A pair no grid serves has **no plan at all** and runs the single
   ProjNet composition it has always run, unchanged.

2. **Shift-free systems are the same systems with the conversion off.**
   `ProjEpsgCatalog` gains `ShiftFreeOf` and `ShiftFreeGeographicBaseOf`: the
   ordinary definition with its datum's WGS 84 conversion zeroed, built through
   the ordinary construction path and cached like every other system. It is not
   a new datum — same ellipsoid, same projection, same axes — so a leg between
   two shift-free systems is arithmetic the engine can reason about, and a leg
   between a catalogued system and a shift-free one is a single datum shift.
   A projected CRS's shift-free copy carries the shift-free base too, so
   *projected → own geographic* is the same projection with no shift in it.

3. **The fallback is ProjNet's, in the direction asked.** Each middle leg is
   `CreateFromCoordinateSystems(catalogued, shift-free pivot)` or its reverse:
   exactly one end carries the conversion. A datum that is the pivot
   contributes no leg, and a catalogue that builds no system for an end
   contributes none rather than a leg with a missing end. Nothing about Helmert
   semantics is re-implemented, so the geodesy the engine delegates stays
   delegated.

4. **The choice is per coordinate, grid first.** For each coordinate the grid
   is asked whether it has a cell covering it; where it does, the shift is the
   grid's, and where it does not, it is the Helmert's. Nothing is extrapolated
   across a block edge (ADR-0105 §7), a geometry crossing an edge is shifted by
   both operations in one request, and a point on the outer edge is outside the
   grid. The Helmert is the fallback *for that coordinate*, so this cannot be a
   per-request decision and is not one.

5. **The plan is cached per host, the walk is the same walk.** Plans are built
   once per CRS pair per `ProjNetTransforms` instance — per instance, not
   statically, because a plan names the grids that host was configured with and
   two hosts in one process may be configured differently. The grid path and
   the ProjNet path share one geometry walk that differs only in what it
   computes for a coordinate, so shapes, empty geometries, layouts, Z/M
   ordinates and the target stamp cannot drift between them.

6. **Cancellation is honoured at the head of the walk and inside it.** The head
   check is the verb's existing one. The per-coordinate walk checks the token
   every 256 coordinates, because a large geometry is a long operation on this
   path and a client that asked to stop should not wait for all of it; a path
   given `CancellationToken.None` never pays for the check.

7. **The method texts change because the truth changed.** The grid candidate's
   text drops its ADR-0105 §applied clause — the verb does apply the grid now —
   and says how the two are chosen instead, because that is the part a client
   needs to read a coordinate. The Helmert candidate's text no longer calls
   itself "not the operation applied", which would now be false over the ground
   the grid covers; it says it is what the verb applies to every point the grid
   does not cover. The Helmert is not superseded by a grid, it is demoted, and
   the difference is worth a word because a method string is the only place a
   client can read it.

8. **Validation is untouched.** Identities are resolved, and the argument
   contract enforced, before a plan is consulted: an unknown CRS, a mismatch
   between a geometry's stamp and `source`, a missing stamp, and a
   non-finite result are the same `invalid.arguments` failures they were. A
   coordinate the plan cannot place is refused rather than returned poisoned.

## §measured

What was measured on this machine, without PROJ:

- **The fallback is unchanged, bit for bit.** A point outside the block is
  answered by the Helmert and equals what a host with nothing deployed returns
  to twelve decimal places — in both directions and from a projected source,
  which is where a non-invertible leg would show. The London control point
  still lands inside 0.1 m of the PROJ 9 reference the repository already
  pins (`ControlPoints.LondonBritishNationalGrid`), so the fallback is still
  the operation the catalogue states an accuracy for.
- **The grid path is the grid and nothing else.** Over the block, a point is
  shifted by the bundle's shift and by no other operation; the projected
  target and projected source legs equal the shift-free projection taken
  straight from the catalogue in the test, so no shift is hiding inside the
  projection.
- **A pair on one datum is not moved.** Both ends on the grid's datum means
  both middle legs are grid-backed and the pair needs no shift; a forward grid
  leg followed by its own inverse lands where it started, and the answer is
  the no-bundle host's answer.
- **The gap was real.** With the production source reverted to ADR-0105 and
  these tests in place, nine of them fail: nothing applied the grid, and the
  method strings still said the verb did not.

## §not measured

Stated plainly, because the temptation is to read a green suite as more than
it is:

- **No PROJ reference run was made; this machine has no PROJ binary.** The
  grid used by the tests is a synthetic constant-shift NTv2 bundle
  (`Ntv2Fixture`), so the suite pins the *mechanism* — grid over the block,
  Helmert outside it, per coordinate — and not the agreement of a published
  bundle such as OSTN15 with PROJ. That agreement is a deployment-and-licence
  exercise (ADR-0105 §licence), still open, and it is not closed by this ADR.
- **The Helmert fallback's agreement with PROJ is inherited, not
  re-measured.** It is pinned against the PROJ 9 reference values already
  committed to the repository, and the fallback answer is unchanged to twelve
  decimals, so the agreement is the one ADR-0027 and ADR-0087 recorded.
- **Only NTv2 is read** (ADR-0105), so a NADCON bundle still has no effect on
  any path.

## Consequences

- A client over ground a bundle covers gets the sub-metre answer the listing
  has been promising, and a client outside it gets the Helmert it always got,
  in the same request and in the same geometry.
- The ADR-0087 §6 invariant — the first candidate is the path `Transform`
  applies — now holds for the grid candidate, which is why its method text can
  stop confessing.
- A transform between two CRSs on a grid-served datum costs a per-coordinate
  decision the Helmert path did not: a bilinear interpolation, or a cell
  lookup that misses, per coordinate. The extra cost is bounded by the grid
  read the registry already caches.
- The catalogue grows shift-free copies of its systems, cached beside the
  real ones. They are implementation detail of this provider and stay inside
  it: no contract type, no ProjNet type and no NTS type crosses a boundary
  (ADR-0005, ADR-0033).
- The TypeScript SDK is unchanged, for the reason ADR-0087 gave: it does not
  model `findTransformations`, and the Esri surface carries the same method
  string the in-process search does. The workbench is unchanged.
- Redeploying a grid is still a restart (ADR-0105 §4), and a plan is built on
  first use, so a host that deploys a bundle after its first transform on that
  pair needs the restart that was always required.
