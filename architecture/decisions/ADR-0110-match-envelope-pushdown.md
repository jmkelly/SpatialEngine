---
status: accepted
date: 2026-09-29
deciders: maintainer + agent
---

# ADR-0110: The feature-match envelope is compiled onto the store's plan, and the in-memory matcher is what verifies it

## Context

ADR-0098 §7 landed the served query plan: `outFields`, `orderByFields`,
`resultOffset`/`resultRecordCount`, `returnCountOnly` and
`returnDistinctValues` are asked of the store, and the Esri `where` grammar
compiles to the plan's predicate (ADR-0074 §7, ADR-0097). That record named
what was left: the *match envelope* — the parameters that decide **which**
features match at all, rather than how the matched set is shaped. SpatialEngine-u2x.11 is that remainder, and it is where the full-table scan actually lived.

`FeatureSpatialMatcher.MatchAsync` was the last path in the GeoServices
facade that read every feature of a layer and decided per feature. It had
`BoundingBox` and a filter string available and used neither: the query
geometry's envelope, `objectIds`, `uniqueIds`, the `time` extent and the
`spatialRel` verb were all evaluated in the adapter, over the whole
dataset, for every request. On PostGIS that is `SELECT <every row>` followed
by a filter in managed code — on a table with a spatial index and a filter
clause, the two things the database is best at. The asymmetry with the
renderer, the MVT encoder, WMS and WFS — all of which already push a box
down (ADR-0098 §7) — was the point: the pushdown existed and was proven, and
one adapter ignored it.

Two constraints made the shape of the change non-obvious.

**1. The pushed read has to be provably the same answer.** The plan is a
*pre-filter*. If the store answered it exactly, the facade would be trusting a
store, and the store would be the answer; principle 15 says pushdown is
optional and must preserve contract semantics, which means the facade has to
be able to say what "the same answer" is. The in-memory matcher is that
definition — so it cannot be the thing the pushdown replaces.

**2. Some of the envelope is refused *by name*, and a narrower read can
silence a refusal.** `spatialRel: esriSpatialRelDisjoint` and `uniqueIds` on a
layer with no string-or-guid identity field are both rejected by
`EsriInteropException` — and both rejections are raised per feature, inside
the match loop. A read restricted to the rows a client asked for can return
*no* feature, and a refusal that only happens when a feature comes back is a
refusal that a pushdown would turn into an empty answer. That is not a
performance question; it is a wrong answer for a valid request.

## Decision

**1. The envelope compiles to the plan, and the compilation is decided before
the store is asked.** `FeatureMatchPushdown` turns the request into a
`FeatureQuery`: `objectIds` becomes the identity restriction (`FeatureId`s of
the layer's own identity column), the Esri `where` clause and the `time`
extent become the attribute predicate, and the query geometry's envelope
becomes the `BoundingBox` pre-filter. The geometry is already the
layer-CRS one — `FeatureProjection.MatchGeometry` transformed it before the
match — so the box the store is asked for and the box the matcher computes are
the same four numbers (ADR-0020's canonical interchange, unchanged).

The decision is all-or-nothing and it is made from the request and the
layer's identity model alone, never from what a store turns out to support. A
request that cannot be compiled is answered by `ScanAndMatchAsync` — the
whole-dataset read that was the only path before — and a request that *can* be
compiled never scans. There is no try-then-scan: a fallback after a pushdown
attempt is the cheapest way to break principle 15, because the answer would
depend on what the store happened to do with the plan.

**2. Two identity rules gate the whole thing**, both inherited from ADR-0097
and neither about dialect support:

- **A layer whose `OBJECTID` is the scan ordinal does not push at all.** A
  read that returned only the matching rows would renumber that key, so the
  same feature would come back with an object id that depends on the query.
  `EsriWhereResolver` already draws this line for the attribute clause; it is
  now the line for the envelope.
- **A `uniqueIds` request is never compiled.** A layer with a durable integer
  `OBJECTID` has no string-or-guid identity column, so `uniqueIds` on it is
  refused by name — per feature. `objectIds` and `uniqueIds` are also
  structurally exclusive here (each needs a different single identity column),
  so there is nothing to compile: the rule keeps the refusal reachable instead
  of turning it into an empty answer on a read that matched nothing. The
  same holds for an unsupported `spatialRel`, which the compiled plan refuses
  to carry for the same reason.

**3. `time` compiles to a disjunction over the layer's date fields, shaped by
the matcher's own rule.** The facade's rule is that a feature matches when
*any* of its date values is inside the inclusive bounds, and that a feature
with **no** date value matches unconditionally — ArcGIS Server ignores `time`
on a layer with no time-aware field. So the pushed predicate is, per date
field, `observed IS NULL OR (observed >= start AND observed <= end)`, OR-ed
across the fields. That keeps every feature the rule can accept: a feature
with no dates because the field is null, and a feature with a date in the
window. The only rows it drops are rows whose every date is outside the
window — which the rule rejects anyway — so the push is a superset and never
loses a match. A layer with no date field compiles no `time` term at all.
A `null` bound is the constant `true` (infinite), as the matcher reads it.

**4. The in-memory matcher is the verification path, and it stays.** Every
row the store returns is still matched by the same `Matches`: the
identities, the `time` extent and the spatial relation (envelope or
topological) are re-tested per feature, so a store that returns more than it
was asked for is caught here rather than served. The one facet the facade
does **not** re-test is the attribute clause, because ADR-0097 already settled
that a store which answered a different row set for it would look right
either way. The matcher was not deleted and was not demoted to a fallback: it
is the definition the pushdown is measured against, and the test fixture
measures both on every query.

**5. The topology relations ride the same box.** Every served `spatialRel`
(`envelopeIntersects`, `intersects`, `contains`, `within`, `touches`,
`overlaps`, `crosses`) implies an intersection, so the envelope pre-filter
admits everything the relation can accept and the relation itself is still
evaluated per feature. The box is therefore a sound pre-filter for all of
them, and no relation needed its own pushdown to stop the table read.

**6. One defect surfaced and is fixed here.** The reference executor's
bounding-box pre-filter read `feature[i].GeometryValue` for a feature whose
geometry attribute is null, which throws — while its own documentation says
"a feature with no geometry (or no envelope) never does", and every SQL back
end answers `null && box` as *not selected*. A layer with nullable geometry
therefore crashed the plan read the moment a box was pushed. The guard is
`!feature[i].IsNull`; the test is the fixture's null-geometry feature.

## Consequences

- **A feature query on PostGIS is an index-usable `SELECT`**: the plan
  compiles to one statement carrying `geom && ST_MakeEnvelope(...)`, the
  identity tuple list and the bound attribute literals, rather than a table
  read the engine filters afterwards. The tests assert it two ways — the
  emitted SQL's shape, and the call count on a live container
  (`ScanAsync` zero, `QueryAsync` one).
- **The results are unchanged, and that is asserted rather than assumed.** One
  conformance fixture of fifteen served queries — every combination of
  `objectIds`, `where`, `time`, the box and the spatial relations, including
  the cases that match nothing, the case with a null date and the case with no
  geometry — is run twice, once through the compiled plan and once through
  `ScanAndMatchAsync`; the two must produce the same matched features in the
  same order and a byte-identical response body.
- **`time` stops keeping the whole dataset in memory** for a layer with date
  fields, and the residual test stays, so the rule the pushdown encodes is
  still the rule the answer comes from.
- **The refusals keep working.** `uniqueIds` on a layer without a string
  identity field and an unsupported `spatialRel` still fail with
  `invalid.parameters`, naming the parameter, even when the request matches no
  feature at all — which is the case a naive pushdown would have answered with
  an empty result.
- **The pushdown is honest about what it is.** A store may still return a
  superset (its own reference evaluator, a fallback store), and the facade
  narrows it; a store may never return a subset it was asked not to, and the
  conformance suite is where that is held.
- **The reference executor is used more than it was**, so its semantics are
  now load-bearing on the served query path rather than only on the in-memory
  stores. The null-geometry guard is the first defect that showed; the
  conformance suite (`Spatial.QueryConformance`) is what keeps it honest.
- **Not decided here:** pushing the match envelope's identity restriction
  through the *paged* served path (`StoreQueryPath`), which still declines a
  request carrying `time`, `objectIds` or `uniqueIds` because a residual
  would be applied after the page was cut; and `uniqueIds` pushdown, which is
  unreachable until a layer can be both integer- and string-identified.

## Alternatives

- **Drop the residual match once the plan is pushed.** Smallest code, and it
  makes the store the answer. Rejected: principle 15 is about the answer, not
  about the cost, and a superset store is then served as if it were exact.
- **Fall back to the scan when a store declines the plan.** Tempting, because
  it makes every store correct by construction. Rejected explicitly: the
  answer would depend on the store's dialect, which is the failure mode ADR-0074
  §4 exists to end, and it would hide a store that ignores a restriction it
  claims to support.
- **Compile `uniqueIds` to a predicate on the unique-id field.** The obvious
  symmetric thing to do, and it is not reachable: a layer's `OBJECTID` is
  integer *or* string-identified, never both, so on the only layers where the
  envelope compiles at all a `uniqueIds` request is already refused by name.
  Recorded here so the next reader does not re-derive it.
- **Push `time` as a plain range test per date field, without the
  `IS NULL` disjunct.** Tighter SQL, and wrong: a feature with a null date
  matches the facade's rule unconditionally, so the tight form would drop
  matches. The disjunction is what makes the push a superset.
- **Keep the envelope in the adapter and only push the box.** What the
  renderer and WFS already do, and it leaves `objectIds` and `time` reading
  the table. The bead's claim is that the *whole* envelope is what stops the
  scan.

## References

- ADR-0098 §7 (the served query plan; this record is the "match envelope"
  it deferred), ADR-0097 (the identity rule and the literal binding),
  ADR-0074 §4/§7/§8 (the plan, result-preserving pushdown, the topology
  verbs), ADR-0037 (the synthetic `OBJECTID` and the scan ordinal),
  ADR-0038 (read-by-identity), ADR-0020 (canonical binary interchange),
  Principles 6, 8, 15, 17.
- `src/Spatial.Adapter.GeoServices/FeatureMatchPushdown.cs` (the
  compilation), `src/Spatial.Adapter.GeoServices/FeatureSpatialMatcher.cs`
  (the compiled and the whole-dataset match),
  `src/Spatial.Querying/FeaturePlanExecutor.cs` (the box pre-filter),
  `tests/unit/Spatial.Adapter.GeoServices.Tests/FeatureMatchPushdownTests.cs`,
  `tests/unit/Spatial.Stores.PostGIS.Tests/PostgisMatchEnvelopeTests.cs`,
  `tests/integration/Spatial.PostGIS.Tests/PostgisMatchPushdownIntegrationTests.cs`,
  `architecture/distilled/contracts.md` (the store read faces).
