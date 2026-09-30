---
status: accepted
date: 2026-09-29
deciders: maintainer + agent
summary: The MapServer and per-feature read surfaces push what the store can answer and keep what it must: identify pushes only the query envelope's box, find pushes only the null test the case-insensitive search cannot be expressed as, generateRenderer asks the store for its minimum/maximum and its distinct set, the layer extent reads a geometry-only projection, and a per-feature target resolves through the identity face — keyed by the `OBJECTID` the row carries, so a store that keys `Feature.Id` differently misses and the scan decides rather than serving the wrong feature.
---

# ADR-0112: The MapServer and per-feature read surfaces push what the store can answer, and the adapter keeps what it must

## Context

ADR-0110 compiled the Feature Service *match envelope* onto the store's plan
and left the in-memory matcher as the verification path. Five surfaces in the
same adapter were still reading every row of a layer and deciding per feature
in managed code, on the same reasoning ADR-0110 rejected for the feature path:

- `MapIdentifyMatcher` scanned the layer and intersected each feature with the
  identify geometry, although the geometry was already parsed, tolerance-buffered
  and reprojected into the layer's CRS;
- `MapFindEngine` scanned the layer and compared the search text against every
  string field of every feature;
- `MapServerResources` scanned the layer to union its features' envelopes into
  the advertised layer extent, carrying every attribute across to drop all but
  the geometry;
- `MapGenerateRenderer` read every value of the classification field to take a
  minimum and a maximum, and every value of the unique-value field to build a
  set — reductions the aggregate face already answers;
- `FeatureResourceReader` and `FeatureAttachmentTargets` scanned the layer to
  find the one feature an `OBJECTID` names.

None of these had a plan, an aggregate, a distinct set or a lookup available to
them at the call site, even where the surface existed. Three of the reductions
(`IFeatureAggregateStore`) and the identity face (`IFeatureLookup`, ADR-0038)
were already in the tree; the call sites had not been moved onto them.

Two of the six call sites could not be moved the way the bead's depth section
anticipated, and the reasons are the substance of this record.

**1. The find text search cannot be pushed as a restriction.** The predicate
vocabulary's only text comparison is `LIKE` (ADR-0074 §2). The engine's
`LIKE` is case-sensitive, and the served `find` search is a case-insensitive
`contains`/`startsWith`. A pushed `'%alp%'` is therefore a **subset** of what
the adapter matches (`ALP` must match `alpha`), not a superset — and a pushdown
has to be a superset or it loses matches. Worse, the direction is
store-dependent: SQL Server's `LIKE` is case-insensitive under the default
collation and Postgres's is not, so the answer would depend on which provider
backs the layer. The first draft of this change pushed the `LIKE` and was
withdrawn by its own test: `find(searchText=ALP)` returned nothing.

**2. A store's `Feature.Id` is not necessarily the layer's `OBJECTID`.**
`IFeatureLookup` is keyed by `Feature.Id` (ADR-0038), and
`EsriObjectIdScheme.ToFeatureId` assumes the engine stores a single integer
identity column as its decimal string. That holds for PostGIS and for a
store written to ADR-0037, and it does **not** hold for a source-identity
GeoJSON ingest: `IngestSchema` numbers features as it reads them, so
`rel.children` ingested with `identity=source&identityField=id` has features
with ids `1..4` and identity values `10..13`. Keying a targeted read's result
by the id the lookup was asked with would then serve object `10` the feature
whose stored id is `10` — a different feature. The hosted relationship tests
(`relate`/`unrelate`) are the ones that caught it.

## Decision

**1. Identify pushes the query geometry's envelope, and only on a layer whose
`OBJECTID` is store-derived.** The pushed box is the envelope of the geometry
the matcher tests, so it admits everything an intersection can accept. The
`layerDefs` definition expression and the temporal selection stay in the
adapter, which is what keeps the `layerDefs` and `dynamicLayers` paths
byte-identical to what they were. A layer whose object id is the scan ordinal
keeps the whole-dataset read (ADR-0097): its `layerDefs` clause resolves the
synthetic `OBJECTID` against the scan ordinal, so a read that returned only the
boxed rows would renumber that key. The intersection test stays the answer over
the rows the store returns, exactly as ADR-0110 left the matcher.

**2. Find pushes the one restriction that is sound, and keeps the text.** The
plan is "at least one of the searched string fields is not null" — a row whose
every searched field is null cannot match, because the match reads a value out
of one of those fields. That is the widest restriction the plan can state
without changing the answer. The search text is not pushed, for the reason
above, and the case-insensitive comparison stays the adapter's. Pushing the
`LIKE` would have been a smaller diff and a wrong answer on any store whose
collation disagrees with the served semantics.

**3. generateRenderer asks the store for the two reductions it was
computing by hand.** The class-break domain is `AggregateAsync` over a minimum
and a maximum of the classification field; the unique-value domain is
`DistinctAsync` over the unique-value field. The `where` clause rides along in
the plan's predicate (ADR-0097) and the quantisation — the equal-interval
breaks, the labels, the palettes — stays in the adapter over the numbers the
store returned, which is ADR-0055's arithmetic unchanged. A request whose
clause no store can read (a synthetic `OBJECTID` on an ordinal-keyed layer)
is not reduced at all: it keeps the scan, because a reduction without the
clause answers a different question. The reduction goes through
`FeatureReductionFallback`, so a store without the aggregate face still answers
from the same plan, in memory, and the empty domain is still the same typed
`invalid.arguments`.

**4. The layer extent is a projected store read.** The plan names the geometry
column as its projection, so the store ships the column the extent is computed
from rather than the whole row. This is a projection, not a reduction: the
aggregate vocabulary has no envelope statistic, so the union is still computed
here over the rows the store returned. A table layer has no geometry column, so
there is nothing to project and the read is the whole-dataset one. Recorded
honestly in the code, and the missing reduction is a follow-up, not a claim.

**5. A per-feature target resolves through the identity face, and a targeted
read that does not resolve one is not evidence of absence.** The fetched rows
are keyed by the `OBJECTID` each one **carries** — the identity column read off
the feature — never by the id the lookup was asked with. A row is therefore
served only when its own identity column is the requested object id, which
makes a mis-keyed store harmless: the row lands under its own id, the request
misses, and the scan decides. A miss then falls back to the scan, because
`IFeatureLookup` says a miss is not an error and a store is free to key its
features by something else; a not-found `OBJECTID` therefore costs the targeted
read *and* the scan it would have cost before, and a resolvable one costs the
targeted read alone. A request naming several ids is one lookup for all of
them, not one per id, and the targets come back in request order.

## Consequences

- **The five surfaces issue a plan, a distinct set, an aggregate or a lookup**,
  and the per-feature ones issue no scan at all when the store can key by
  identity. `MapResourcePushdownTests` asserts both halves on every one: the
  store was asked the bounded read, and the response is byte-identical to the
  one a store handing back the whole layer produced — which is exactly what the
  pre-change code saw. The `layerDefs`, temporal and `dynamicLayers` paths are
  asserted unchanged, including the ordinal-keyed layer that keeps its scan.
- **The refusals still happen.** An unknown `OBJECTID` is still `not.found`
  with the same message, a non-integer identity column is still
  `serverError`, an empty class-break or unique-value domain is still the
  typed `invalid.arguments` naming the layer, and a cancelled request still
  cancels.
- **The verification path is a *hit*, not a fallback.** This is the one place
  the shape differs from ADR-0110, and the reason is specific: a plan is
  answered by a store that honours it, but a lookup is keyed by a value the
  store may not be using as its key. Keying the result by the identity column
  turns "the store answered a different question" into "the store did not
  answer this one", which the scan can. No answer depends on which store is
  behind the layer.
- **The find pushdown is small on purpose.** It removes rows with no searchable
  value and nothing else. What would remove the rest is a comparison operator
  the vocabulary does not have — a case-folding `LIKE` the back ends agree on —
  which is a contract change and is filed as a follow-up rather than smuggled in
  here.
- **The layer extent is projected, not reduced.** The columns that are not
  geometry no longer cross, but every row still does. The reduction the
  bead wanted is a missing aggregate statistic; the follow-up says so.

## Alternatives

- **Push the find search as `LIKE` and keep the adapter's match as a second
  filter.** Looks like the same shape as everything else here, and is wrong:
  the second filter can only remove what the first left, so a case-sensitive
  store silently loses the case-insensitive matches. Withdrawn by its test.
- **Push the find search as `LIKE` and drop the adapter's match**, letting the
  store's collation define the search. Rejected: it makes the served answer
  depend on the provider, which is the outcome ADR-0074 §4 exists to end.
- **Treat a lookup miss as `not.found`.** Cheaper, and wrong for every store
  that does not key `Feature.Id` by the identity column — the source-identity
  ingest among them, which is the default way a GeoJSON is loaded. It turns a
  store's key convention into a served 404.
- **Add an envelope statistic to the aggregate vocabulary** and reduce the
  layer extent at the store. The right end state, but it is a change to
  `Spatial.Core`'s `AggregateStatistic` plus every store, which is a contract
  change of its own and not this bead's files. Filed as a follow-up.
- **Key the lookup result by the requested id** and trust the store. Simplest,
  and it is the defect in point 2: a mis-keyed store would serve a different
  feature under the right object id, with no error anywhere.

## References

- ADR-0110 (the match envelope pushdown this extends), ADR-0098 §7 (the served
  query plan), ADR-0097 (the identity rule for a pushed clause), ADR-0083
  (aggregate pushdown), ADR-0074 §2/§4/§6/§7/§8 (the predicate vocabulary,
  result-preserving pushdown, reductions), ADR-0038 (read-by-identity),
  ADR-0037 (the synthetic `OBJECTID`), ADR-0055 (renderer quantisation),
  ADR-0048 (identify's tolerance and precision), ADR-0020 (canonical binary
  interchange), Principles 6, 8, 15, 17.
- `src/Spatial.Adapter.GeoServices/MapMatchPushdown.cs` (the identify and find
  compilation), `MapIdentifyMatcher.cs`, `MapFindEngine.cs`,
  `MapGenerateRenderer.cs`, `MapServerResources.cs`,
  `FeatureAttachmentTargets.cs`, `FeatureResourceReader.cs`,
  `tests/unit/Spatial.Adapter.GeoServices.Tests/MapResourcePushdownTests.cs`,
  `architecture/distilled/contracts.md` (the store read faces).
