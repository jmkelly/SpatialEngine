---
status: proposed
date: 2026-09-13
deciders: maintainer + agent
summary: Geometry measurement, processing and relation verbs are separate SDK interfaces.
---

# ADR-0036: Geometry measurement, processing and relation verbs are separate SDK interfaces

## Context

ADR-0035 requires the GeoServices adapter to map protocol verbs onto engine
verbs and to keep spatial algorithms out of the adapter. The GeoServices
Geometry Service (spec §7) needs more than `IGeometryOperations`' four verbs
(`buffer`, `intersection`, `validate`, `simplify`): `areasAndLengths`,
`lengths`, `distance`, `labelPoints`, `convexHull`, `difference`, `union`,
`densify` and `relation`. `IGeometryOperations` carries the one verb two
GeoServices names share: both `generalize` (§7.0.13) and `simplify`
(§7.0.5) are Douglas-Peucker generalization, and its `Simplify` is exactly
that. Topological repair is wanted too — no Esri operation names it, so
nothing in the protocol maps to it.

The geoservices-implementation-plan §5 leaves the interface granularity open
between "one extended `IGeometryOperations`" and split faces.

## Decision

Add three focused SDK interfaces over core geometry values, implemented by
`Spatial.Operations.NetTopologySuite`:

- `IGeometryMeasures` — `Area`, `Length`, `Distance`, `LabelPoint`.
- `IGeometryProcessing` — `Union`, `Difference`, `ConvexHull`, `Repair`,
  `Densify`. `Repair` is the topological MakeValid-equivalent; it is
  deliberately distinct from `IGeometryOperations.Simplify`.
- `IGeometryRelations` — `Relate` (DE-9IM intersection pattern).

The faces stay granular rather than growing `IGeometryOperations` so that an
implementation advertises exactly the capability it owns, and so the
adapter's `as`-free composition mirrors the granularity. All verbs are pure,
planar and cancellable, and all accept and return only `Spatial.Core`
geometry values (ADR-0005).

The GeoServices adapter maps:

| GeoServices | Verb |
| --- | --- |
| `areasAndLengths`, `lengths`, `distance`, `labelPoints` | `IGeometryMeasures` |
| `convexHull`, `difference`, `union`, `densify` | `IGeometryProcessing` |
| `relation` | `IGeometryRelations` |
| Feature Service `spatialRel` (`Contains`/`Within`/`Touches`/`Overlaps`/`Crosses`) | `IGeometryRelations` (DE-9IM patterns, envelope-prefiltered) |
| `project` | `ICoordinateTransforms` |
| `generalize`, `simplify` (`deviation`/`value`), `maxAllowableOffset`, `quantizationParameters`, `buffer`, `intersect` | `IGeometryOperations` |

The Geometry Service's two operations take an algorithm tolerance and go
through `Simplify`; the Feature Service's two query parameters state a
deviation budget and go through `Generalize` (ADR-0079). The split is
deliberate: a tolerance the caller picks for the algorithm and an allowance
the caller grants the answer are not the same number, and one method cannot
carry both without the two being swapped at a call site.

## Consequences

- Three additive interfaces in `Spatial.PluginSdk` and three additive
  implementations in `Spatial.Operations.NetTopologySuite`, registered by
  `Spatial.Host`. No existing contract changes.
- The name trap is resolved structurally: `generalize` and `simplify` both
  call `Simplify`, each with its own tolerance parameter (`maxDeviation`,
  `deviation`/`value`), with a regression test that a self-intersecting ring
  is thinned rather than repaired.
- `Repair` stays an engine verb with no protocol name. It uses NTS
  `GeometryFixer` (the OGC MakeValid port) and splits a self-intersecting
  ring into its valid parts, reachable through the engine API only: an Esri
  operation that aliased it to `simplify` would generalize nothing and
  repair instead, which is what the wiring used to do.
- New verbs are still pure and synchronous; long work remains the caller's
  cancellable request (ADR-0033).
- `Relate` is the one face the query path uses as well as the geometry
  service: the Feature Service `spatialRel` verbs are its DE-9IM patterns
  (`T*****FF*` contains, `T*F**F***` within, `FT*******` / `F**T*****` /
  `F***T****` touches, and the overlaps and crosses variants selected by
  geometry dimension),
  with the envelope tests kept as the pre-filter so the exact predicate runs
  only on candidates. No contract change — the interface was already
  registered; only the adapter's dependency set grew.
  (SpatialEngine-u2x.35 corrects the touches row: the dimension-keyed pick
  dropped position 2, so a point or line feature on a query polygon's
  boundary read as not touching. The three OGC touches masks are the
  dimension-free union, and the union is not one nine-character pattern.)
- The dimension-dependent verbs are keyed on the **dimension pair**, not on
  which side is higher. `Overlaps` is `T*T***T**` for A/A and `1*T***T**`
  for L/L — the interiors must meet in dimension one, so two lines sharing a
  span overlap and two crossing lines do not — and `Crosses` is `T**T*****`
  (A/L), `T*T******` (L/A) and `0********` (L/L), where the interiors must
  meet in dimension zero. A pair the reference does not relate at those
  dimensions reads false rather than asking a pattern. (SpatialEngine-u2x.56
  corrects both rows: one `Overlaps` pattern over both same-dimension cases
  read a crossing line pair as an overlap, and the equal-dimension gate on
  `Crosses` meant a crossing line pair never crossed.)
- The DE-9IM **grammar** is `Spatial.Core.Geometry.De9imPattern` and the one
  place that states it: nine cells, each `T`, `F`, `0`, `1`, `2` or `*`. It
  is checked in both directions, because both directions get it wrong
  separately. The verb rejects a pattern it cannot answer with
  `invalid.arguments` naming the grammar, rather than passing it to
  NetTopologySuite, which rejects a wrong *length* with a provider message
  about a length and reads a cell it does not recognise (`X`, `E`, a space)
  as a constraint that quietly fails — so `T*T***T*X` answers false where the
  caller meant `T*T***T**`: a parameter accepted and ignored. The GeoServices
  boundary reads the same grammar to tell a client's pattern from a client's
  relation *name*, and its own narrower reading (`T`, `F`, `*`, `0` only)
  rejected every pattern naming a dimension — `1*T***T**`, the line/line
  overlap pattern this record's own table serves — by name
  (SpatialEngine-imj).
- A pattern is answered **exactly as the intersection matrix reads**, cell by
  cell, in every position and for every cell symbol. SpatialEngine-imj was
  opened to reconcile a wildcard reading with the exact one and found nothing
  to reconcile: a sweep of 1.2 million pattern evaluations over 20,449
  geometry pairs (polygons, polygons with holes, boundary and corner points,
  multi-geometries, collections, empties) found no case where the two
  disagree, and the pinned cases in `NtsGeometryRelationsTests` read their
  matrices from the matrix string, not from the matcher. The report's
  examples were two grammar slips — `"**T**"` is not a nine-cell pattern, and
  `"1*2F0*1*2"` constrains position 4 to `F` where the matrix reads `0` (the
  position numbering runs interior∩interior, interior∩boundary,
  interior∩exterior, boundary∩interior, so position 4 is the square's
  *boundary* against the line's interior, which the crossing line does
  reach). Every pattern the adapter serves is pinned against a hand-computed
  matrix over all 256 ordered fixture pairs, so a pattern that means a
  different cell than it names fails there.
- The served patterns have **two** independent readers, not one, and the
  reason is that neither can do the other's job. `SpatialRelationMatrix` is
  where a fixture and its expected verdict are written down once so the
  Feature Service query path and the Geometry Service `relation` operation
  are held to the same row; it is the served table's own second copy, and a
  table cannot cross-check itself. `FeatureSpatialRelationTests` derives each
  pair's matrix from nine single-cell questions and reads the served patterns
  over that derivation, and it is the reader that checks the *hand-written*
  column of the table as well — which is how two of the three line-along-the
  -edge rows were found to carry a matrix that was not the pair's. Neither
  reader replaces the other, and the oracle is not folded into the table
  (ADR-0156, which amends this record's reading of how the DE-9IM patterns
  are checked).
- The **DE-9IM vocabulary gap** for a point on a line is documented, not
  papered over. A point's own boundary is empty, so a point sitting on a line
  — at an endpoint or in its interior — reaches the matrix only in the
  line's boundary against the point's interior, and no single nine-cell
  pattern names "every point of B lies in the closed set A". `Contains` and
  `Within` both read false for that pair; the OGC intersect union and the
  contact mask read true, and NetTopologySuite's own `Touches` agrees with
  the contact reading. A `covers`-style alternative is deliberately **not**
  offered: covers is the *negation* of the disjoint pattern rather than a
  pattern, so it is not reachable through this interface, and no relation
  name the served protocols define asks it. Inventing a verb to answer a
  question DE-9IM does not have would be a second, looser notion of "meets"
  next to the exact one (SpatialEngine-imj).
- The verb's **fidelity to the reference implementation** is characterised
  over the point, line and area combinations the Esri surfaces serve, and no
  served predicate depends on a deviation. NetTopologySuite 2.6 renders the
  canonical nine-cell matrix for every one of them — a point on a polygon's
  boundary is `FF20F1FF2` and a point in its interior `0F2FF1FF2`, cell for
  cell what the matrix is — and renders a pair's matrix in one operand order
  as the transpose of the other, which the query path needs because it
  compares a feature with a query in whichever order the caller wrote them.
  A pattern is answered over that matrix cell by cell, so the two readings
  cannot drift. The served dimension-keyed table and the reference's own
  named predicates (`Contains`, `Within`, `Touches`, `Overlaps`, `Crosses`,
  `Intersects`) agree on all six verbs over every ordered pair of the served
  fixtures (SpatialEngine-1dg). Two reports of a divergence do not survive
  that sweep, and both were readings rather than behaviour: a rendered matrix
  read as a non-canonical one (the strings reported, `F0FFFF212` and
  `0FFFFF212`, are not the matrices of the pairs named, and the hand-computed
  columns they were compared against — `FTFFFFFFT` and `0FFTFFTTT` — are not
  matrices either, since a point's own boundary is empty and every cell of its
  boundary row is `F` whatever the other geometry does); and the reference's
  typed predicates read against a *dimension-blind union* of the OGC
  alternation, which asks a question the standard does not ask a pair that
  involves a point. That union is what this record's dimension gate exists to
  prevent: a crossing line against a polygon overlaps under it, and reads as
  both `Overlaps` and `Crosses` (SpatialEngine-u2x.56).
  `NtsGeometryRelationsFidelityTests` holds the hand-computed table, the
  transpose property and the agreement sweep.
- The *reading* of that table — which pattern answers which verb, and what
  the point/area case is — is ADR-0106: the OGC table verbatim, the feature
  geometry as the left operand, and a dimension-selected pattern only for the
  verbs OGC defines per dimension pair. ADR-0036 named the patterns and its
  later corrections; ADR-0106 says which reading of them is served, and why
  the discarded reading was not.

## References

- ADR-0033 (in-process service interfaces)
- ADR-0035 (GeoServices boundary adapter)
- `architecture/geoservices-implementation-plan.md` §5 (S1b)
- `architecture/distilled/contracts.md`
