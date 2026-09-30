---
status: accepted
date: 2026-09-29
deciders: maintainer + agent
summary: An area of use is a set of rectangles, so an extent that crosses the antimeridian (EPSG 1175, 2157) is the two rectangles it is rather than an empty one; the graph's intersection and union are rectangle algebra over that set and never merge, and `findTransformations` publishes `areaOfUse` as a list of envelopes (amends 0086, 0087).
amends: ADR-0086, ADR-0087
---

# ADR-0111: An area of use is a set of rectangles, so a wrapped extent is not an empty one

## Context

ADR-0086 puts each datum's area of use in `CrsAreaOfUse`, and ADR-0087
gives the graph one rule per shape of operation: a direct shift is valid
where both datums apply (the intersection), a concatenated one wherever
either step applies (the union). Both rules were rectangle algebra over a
single rectangle, and the rectangle could not say what EPSG publishes.

EPSG writes an extent that crosses the antimeridian by giving a west bound
in the east and an east bound in the west. Extent 1175 "New Zealand" is
160.6E to 171.2W; extent 2157 the Aleutians is 164.84E to 164.84W. Read
verbatim, `160.6 > -171.2`, and an area of use whose west is east of its
east is the empty rectangle — the one shape the graph already means by
"this operation is valid nowhere". So there were two ways to be wrong and
the catalogue had chosen between them by hand:

- Taken verbatim, the whole of New Zealand is an empty area, every
  candidate built on it is dropped by `Covers`, and NZGD2000 is not in the
  transformation graph at all. Nothing reports the loss: a search for a
  datum with no candidates is an ordinary answer.
- Clipped at 180 — which is what the row did — the node survives and the
  registered ground west of the antimeridian, the Chathams, is outside
  every area of use that mentions New Zealand. A search over the Chatham
  Islands returns no direct operation, and the ground is covered by a
  1.0 m registered shift the catalogue already holds.

`Intersect` and `Union` were the same story from the other side. Both took
`min`/`max` of the bounds, which is the bounding box of the operands, and
a bounding box is not an area of use: two datums whose extents overlap in
two places produced a rectangle covering the sea between them, and a
wrapped operand took part in a composition it silently widened — union a
wrapped extent with any European one and the min of the western box's
`-180` against the eastern box's `180` is a rectangle running the whole
width of the world.

Three shapes were on the table. A **signed span in degrees east** keeps
one rectangle and writes New Zealand's east bound as 188.8, outside
[-180, 180]; it needs a frame for every comparison, and it is still one
interval, so intersecting a wrapped extent with anything loses one of its
two halves. An **explicit `crossesAntimeridian` flag** keeps the bounds
EPSG published and tells a capable client to do the work; a client that
does not read the flag computes the empty rectangle, which is the defect
with a second spelling. The **split pair of rectangles** is the only one
that keeps both halves under ordinary algebra.

## Decision

1. **An area of use is a set of rectangles, not a rectangle.**
   `CrsAreaOfUse` becomes `(string Name, IReadOnlyList<CrsAreaOfUseBox>
   Boxes)`, and `CrsAreaOfUseBox` is `(XMin, YMin, XMax, YMax)` in degrees.
   `CrsAreaOfUse.One(name, …)` builds the ordinary one-box case; **no
   boxes** is the empty area. The invariant is that every box is a real
   rectangle — west not east of east, longitudes in [-180, 180] — so
   emptiness is the absence of boxes and never an inference from a pair of
   numbers that happens to be the wrong way round. The type is a record and
   the record's equality is overridden to compare the boxes: the generated
   one compares the list by reference, which would call two identical
   areas unequal and two different ones equal, in a type the graph, the
   adapter and the tests all pass around.
2. **A wrapped extent is the two rectangles it is.** `EpsgDatumOperations`
   carries `double[][] AreaOfUseBounds` — one array per rectangle — and
   extent 1175 is read as `[[160.6, -55.95, 180.0, -25.88], [-180.0,
   -55.95, -171.2, -25.88]]`: the registered latitudes and the registered
   west and east bounds on each side of the seam, neither clipped and
   neither invented. Every catalogue row is still a transcription of one
   EPSG operation and one EPSG extent, and the test pins both halves.
3. **The set operations are rectangle algebra, and they never merge.**
   `Intersect` intersects every box of one set with every box of the other
   and keeps the non-empty results; `Union` concatenates them and drops
   the boxes another box already covers; `Contains` holds a candidate when
   every box of the interest is inside some box of the candidate's area.
   Merging is refused deliberately: the only two boxes a merge would join
   are the ones either side of the antimeridian, and joining those
   fabricates a rectangle from 160.6E to 171.2W — the wrapped reading this
   record exists to remove. Nothing merges, so nothing can.
   `Union(NZ, World)` is `World`: New Zealand is dropped because the world
   covers it, not because it was never there.
4. **The empty area is still a real answer.** An empty intersection still
   drops the direct candidate, so NZGD2000 against NAD83 comes back with
   only the path through WGS 84, and `IsEmpty` moves out of
   `HelmertAlgebra` — it is a fact about areas, not about shifts — onto
   the graph next to the operations that produce it.
5. **A request may be a set too.** `extentOfInterest` is split at the
   antimeridian when it crosses it rather than sorted into one interval.
   Reprojection does not know the world has a seam (a New Zealand extent
   over EPSG:2193 comes back with a west of 165E and an east of 172W) and
   neither do clients that paste a wrapped extent from a map. Sorting the
   two bounds turns the smallest legitimate request into the largest
   possible one — the whole Pacific — and the filter then returns
   everything.
6. **The published listing is a list of envelopes.**
   `findTransformations` publishes `areaOfUse` as an array of
   `{name, xmin, ymin, xmax, ymax}` — one entry per rectangle, the
   Envelope shape the spec sketches — rather than the single object it
   sketches. One envelope cannot carry 160.6E to 171.2W: clipped it lies
   about the extent, and unclipped it is the empty box. A second and
   subsequent entry names the side it is on ("… , west of the
   antimeridian") so a client reading the list can tell the halves apart,
   which is the one thing it could not do before. A single-rectangle area
   — which is nearly all of them — is a one-element list.
7. **The area-of-use name stays a short label, and the registry's
   wording stays with the row.** EPSG's extent names run to sentences
   ("North America - Canada and USA (CONUS, Alaska mainland)"), the name
   is composed into client-facing strings, and half the areas of use in a
   listing are *composed* ones — "the Great Britain and France areas of
   use" — that name no EPSG extent at all, so a field carrying the
   verbatim extent name would be a lie for every composed area. The label
   is a **shortening and never a rename**: `DatumOperation` now carries
   `ExtentName`, the registry's own wording beside the label, and a test
   holds every label to being a substring of it. What a row was read from
   is the row's business, as ADR-0086 decided, and the record the graph
   publishes says only what the ground is.
8. **A union is a set of rectangles, not their bounding box.** This
   changes published geometry beyond the wrapped cases: a concatenation
   through WGS 84 between two datums with disjoint extents now publishes
   the two extents rather than one rectangle around both, which is a
   narrower claim and the true one.

## Consequences

- New Zealand is in the transformation graph over the ground EPSG says it
  is used over, including west of the antimeridian. The reproduction is
  `WrappedAreaOfUseTests.New_zealand_is_in_the_graph_over_the_ground_west_of_the_antimeridian`:
  a search over the Chathams used to return no direct operation and now
  returns the one EPSG:1565 registers at 1.0 m.
- No operand can poison a composition any more, and there are three tests
  for it rather than an argument: intersecting a wrapped extent with the
  world keeps both halves, two extents that meet only across the seam
  still intersect, and a union with a wrapped operand does not span the
  planet.
- `areaOfUse` in a `findTransformations` response is an array. That is a
  caller-visible change to an Esri-interop shape, and the single
  rectangle a client was reading is `areaOfUse[0]`. The one-element list
  is deliberate: a field that is an object for one datum and an array for
  the next is a client bug waiting to happen.
- `CrsAreaOfUse` is a contract change (`Spatial.Contracts` takes only
  core types, and a list of records of doubles still does), so any future
  provider implementing `ICrsDirectory` writes the set algebra itself.
  That is the intended pressure: the rules are EPSG's, not the adapter's.
- NZGD2000 still serves no transformation through the service, because
  its vendored WKT carries no `TOWGS84` and the graph composes shifts out
  of definitions. That is a gap in the *parameters* of EPSG:1565, not in
  its extent, and it is not fixed here — the extent is now right, and the
  parameters are a separate record to make.
- The union change narrows published areas in cases nobody has measured.
  A client that drew the published rectangle drew more ground than the
  operation stands for; it now draws what the operation stands for.

## Rejected

- **A signed span in degrees east** (New Zealand as 160.6E–188.8E). One
  number per bound and no schema change, but every comparison needs a
  frame, and a single interval still cannot hold the intersection of a
  wrapped extent with anything — the defect would survive, one operation
  later.
- **An explicit `crossesAntimeridian` flag.** Cheap, and it leaves the
  bounds EPSG published rather than the numbers a naive read misuses — but
  a client that ignores the flag still computes the empty rectangle, so
  the flag is a fix that only reaches clients that already knew to look
  for one.
- **Clipping the east bound at 180, kept as the documented behaviour.** It
  is what the row did, and it is a claim the extent record does not
  support: it publishes ground the registered operation does not cover
  and drops ground it does.
- **Publishing the verbatim extent name.** Composed areas name no extent,
  so the field would be right only for the datums and wrong for the
  operations — and wrong in the direction a client trusts more.
