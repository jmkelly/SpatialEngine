# ADR-0074: Datum transformations are values, and `findTransformations` is a search

Status: Accepted

## Context

`findTransformations` (Geometry Service, spec §7.0.9) shipped as a
reformulation of the catalogue rather than a search. Same datum → `[]`; a
different datum → exactly one forward composite naming a Helmert path. Two
arguments were rejected *by name*: `extentOfInterest`, because "the catalogue
has no area-of-use model to rank transformations", and `vertical`, because
"the curated catalogue carries horizontal CRSs only". `project` rejected every
client-supplied `datumTransformation`.

The rejections were honest about the symptom and wrong about the cause. The
underlying capability was already there — every geographic definition in the
curated catalogue carries its TOWGS84 parameters, and OSGB36 uses the classic
seven-parameter Helmert (ADR-0027) — but the parameters were welded to the
adapter, so the search had exactly one possible answer to give and the adapter
rather than the catalogue had to be asked what a transformation was.

A second, quieter problem sat underneath: the same path was described
differently depending on who asked. A client asking 4326→27700 was told
"WGS 84 to Ordnance Survey 1936 via a Helmert on the transform path"; a client
asking 27700→4326 got the same sentence with the words the other way round. The
parameters behind the sentence were never published, so nothing outside the
adapter could check them.

## Decision

1. **Operations are contract values.** A new
   `Spatial.Contracts.TransformationSearch` namespace gains
   `CrsTransformation` (name, method, steps, area of use, stated accuracy,
   approximate), `CrsTransformationStep` (name, direction, method,
   `HelmertParameters`), `HelmertParameters` (the seven EPSG-9606
   position-vector terms), `CrsAreaOfUse` and `CrsTransformationQuery`. They
   are records of strings, doubles and booleans — core/framework types only,
   no geometry and no third-party type (ADR-0005, ADR-0033). `ICrsDirectory`
   gains `FindTransformations(query, token)`: candidates between two CRSs,
   ranked best-accuracy first, filtered by an optional area of interest. The
   search DTOs sit beside `Spatial.Contracts.Transformations` (the
   description face) rather than inside it: describing a CRS should not
   couple a caller to a transformation graph, and the namespace-level
   coupling metrics agree — merged in, the description namespace crosses the
   `.dependably` architectural-rigidity gate at high severity; separate, both
   stay under it.
2. **The graph lives with the catalogue, not the adapter.**
   `Spatial.Transformations.ProjNet` composes operations from the datum
   definitions it already holds. Every datum node carries its shift to WGS 84,
   the accuracy of that shift and the area it is used over, so the graph is a
   hub and spokes rather than a table of hand-written pairs, and a projected
   CRS and its geographic parent return the same candidates. Between two
   datums it offers the **direct** shift (one step, the composed parameters —
   the path the engine actually applies), the **concatenated** path through
   the WGS 84 pivot (each datum's own step, in order), and the same shift
   **reduced to three translations** for toolchains that cannot carry
   rotations or scale. A pair whose composed shift is the identity (ETRS89
   against NAD83) yields nothing, because there is no operation to publish.
3. **Accuracies are derived, not asserted.** The two datums' accuracies
   combine in quadrature, as independent errors do. The reduced form adds the
   first-order bound on what its dropped rotations and scale cost —
   `‖A − s·I‖·R` at the mean Earth radius — which is metres for OSGB36 and
   nothing for the datums that have no rotations. No accuracy in the listing
   is a round number picked for convenience.
4. **Area of use follows the EPSG rule for the shape of the operation**: a
   direct shift is valid where both datums apply (the intersection), a
   concatenated operation wherever either step applies (the union). An empty
   intersection drops the direct operation outright, which is why
   OSGB36-to-NAD83 comes back with only the path chained through WGS 84.
   `extentOfInterest` filters on that: candidates whose area of use does not
   cover the requested extent are not returned. This is the difference
   between "we have nothing for France" and "here is the one operation valid
   everywhere, and here is why it is less local".
5. **The search is symmetric.** Operations are published in a canonical datum
   order (the WGS 84 pivot first, then by code), so both directions name the
   same operation with the same parameters; a reversed request runs the steps
   in reverse order marked `transformForward: false`. The parameters stay the
   ones the operation is defined by — an inverse Helmert is the client's to
   apply, not a second set for the service to invent.
6. **The first candidate is the applied path.** `project` therefore accepts a
   `datumTransformation` that names it, and refuses any other name by naming
   the operation it does apply. The refusal is not a shrug: the catalogue
   carries Helmert operations only, so the alternatives cannot be projected
   in its place without silently changing the result. The Feature Service
   query path keeps its own rejection — that is the query contract, not this
   search.
7. **The published parameters are the engine's parameters, and a control point
   says so.** `Spatial.Transformations.ProjNet` applies the geocentric path
   (source ellipsoid → TOWGS84 forward → target TOWGS84 in reverse), which is
   the composed Helmert the graph publishes. The tests prove it
   independently: they take the parameters the graph returns for WGS 84 to
   OSGB 36, apply them to the London control point by hand in the test, and
   project the result — landing within a millimetre of the PROJ 9 (OSTN15)
   reference the existing control-point tests already use. The reduced form's
   miss is 14.5 m against a stated 41.9 m, so the bound is conservative in the
   direction it should be.
8. **`vertical=false` is accepted** — it is what every client sends and what
   a horizontal catalogue answers. `vertical=true` asks for vertical
   transformations the catalogue does not carry and stays refused by name.
   `numOfResults` and `numTransformations` (the ArcGIS REST JS spelling) are
   both read; the default is every ranked candidate, because a search that
   hides its second-best answer behind a default is not a search.

Grid-shift operations (NTv2/NADCON) are the follow-on and are not in this
slice. The graph is the structure that would host them: a step is already a
value with its own method, and a grid step would be a third candidate kind
with its own area of use and accuracy.

## Consequences

- `findTransformations` answers a question instead of restating a fact. A
  client over France and a client over Britain get different lists from the
  same catalogue, both correct, and each candidate says what it costs and
  where it applies.
- The parameters are checkable from outside. The reconciliation test applies
  the published numbers itself; an implementation that published decorative
  parameters would fail, and one that published the wrong sign or a doubled
  scale — the bug this slice's own round-trip test caught — fails with it.
- `ICrsDirectory` grows a method, so any future provider must implement a
  transformation graph, not just describe and transform. That is the intended
  pressure: `Describe` alone cannot answer a search.
- The catalogue is still horizontal and still Helmert-only, and the listing
  says so in every method string for OSGB36.
- The host HTTP surface and the TypeScript SDK are deliberately unchanged: the
  search is served on the Esri interop surface, which is where clients ask the
  question, and adding a `/api/crs/transformations` route would publish a
  second spelling of an answer the contract already carries in process.
