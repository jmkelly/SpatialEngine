---
status: accepted
date: 2026-10-04
deciders: maintainer + agent
summary: The map root advertises `supportsTimeRelation:true` exactly when one of its layers designates the dates bounding a feature, and on such a layer export and identify serve `esriTimeRelationContains` and `esriTimeRelationWithin` as themselves — the parsed relation rides the window to the matcher instead of being discarded; a layer that designates none keeps refusing them by name (amends 0100, 0175).
amends: ADR-0100, ADR-0175
related: ADR-0081
consulted: ADR-0178
---

# ADR-0182: Serve the contains and within relations on a designated layer

## Context

ADR-0100 rejected `esriTimeRelationContains` and `esriTimeRelationWithin` and
turned `supportsTimeRelation` off, because the code accepted the parsed
relation on both paths and threw it away — `MapExportLayers.cs:38` and
`MapIdentifyPlan.cs:31` both read it and discarded it, so the two relations
were served as overlaps while the root advertised `true`. The record named the
missing piece rather than a missing line: applying the relation needs **a
feature temporal extent**, and deciding what one is was filed as its own
record.

ADR-0175 decided that — an extent is the interval between the two date fields
a layer's schema designates, and one rule in `Spatial.Core` evaluates all
three relations over it — and ADR-0178 implemented it. Two things were still
missing, and they are what this bead found. The rule is applied, but the
relation is never named: every reader calls
`TemporalExtent.From(feature, fields).Overlaps(window)`, because the
parameter that chose the relation is still parsed and discarded, and the
window the readers receive (`MapTimeExtent`) has nowhere to put it. And
`supportsTimeRelation` is still hard-coded `false`, so the flag is not
describing the surface even where the surface can now answer all three.

The flag question is the sharper one. ADR-0081's rule is that a capability flag
is advertised exactly when the behaviour proves it, and ADR-0175 §6 deferred
this decision explicitly: `false` "through the implementation", and `true`
only "when a layer with a designation can answer all three relations". So the
flag is not a global property of the engine — it is a property of the service
being served, and a service is a list of layers with descriptions already in
hand at the root.

## Decision

**Serve the relation the request named on a layer that designates one, and
advertise `supportsTimeRelation` exactly when a served layer designates one.**

**1. The relation rides the window to the reader.** `MapTimeExtent` — the
render contract's temporal extent, already resolved per layer at the request
edge — gains a non-positional init-only `Relation` member defaulting to
`TemporalRelation.Overlaps`, so an existing construction and an existing
caller mean what they meant and the readers cannot drift. Non-positional for
the reason ADR-0178 used for `DatasetDescription.TimeFields`: record equality,
deconstruction and every existing construction stay untouched. The relation is
a `Spatial.Core` type, which is what contracts may carry.

**2. Both edges parse it and both mean it.** `MapExportTime.ParseTimeRelation`
returns a `TemporalRelation?` — blank is the default overlaps, the three
documented names are read case-insensitively and trimmed, and anything else is
still a typed `invalid.arguments` naming the three it does serve.
`MapExportLayers` and `MapIdentifyPlan` pass it to `ResolveTimes`, which stamps
it on every window it resolves. Nothing parses a value and throws it away, so
the ADR-0100 failure cannot recur by a call site quietly re-ignoring the
result.

**3. A layer with no designation refuses the two relations by name, at the
edge.** `MapExportTime.RequireDesignated` throws the same typed
`invalid.arguments` naming the relation, the layer id and the layer's name,
when a request asks for contains or within over a layer whose description
carries no designation. Export reads the designations only when such a relation
was asked for, so the default path describes nothing it did not before. The
refusal is per layer rather than per service, because a service may mix a
designated layer with one that is not and a client naming the layer is asking
about that layer.

**4. The readers apply the relation they are given.**
`FeatureSpatialMatcher.MatchesTime` takes the relation and calls
`TemporalExtent.Matches(window, relation)`, so the query path and the identify
path (which delegates) share the one rule; `FeaturePipeline.MatchesTime` does
the same for the render path from `MapTimeExtent.Relation`. The undesignated
branch of each is a backstop rather than a path: a relation other than
overlaps reaching a layer that designates none throws (`GeoServicesErrors`
`invalid.arguments` in the adapter, `SpatialException.BadArguments` in the
renderer, which may not reference the adapter) rather than answering with the
bag rule. A parameter is honoured or refused, never accepted and ignored.

**5. The root advertises the flag when a served layer earns it.**
`MapServerResources.Root` reads `supportsTimeRelation` as "at least one of the
served layers' descriptions carries a non-empty designation". The descriptions
are already in hand — the root takes `MapLayerInfo`, which holds the
`DatasetDescription` it projects `drawingInfo` from — so this is a read, not a
lookup. A service whose layers designate none keeps the ADR-0100 flag and its
typed rejects, which is the whole surface honestly described.

The pushed pre-filter is untouched: ADR-0175 §5 keeps it in the overlaps shape
for all three relations, because contains and within both imply overlap.

## Alternatives

- **Keep `ParseTimeRelation` returning a string and map it to the enum at each
  call site.** Rejected: two call sites today is two translations to keep in
  agreement, and the failure mode is precisely the one this record exists to
  close — a call site that parses and then does not translate serves the
  default relation whatever the client asked for.
- **Add the relation as a positional parameter to `MapTimeExtent`.** Rejected:
  it breaks every construction, `with`-expression and deconstruction of a
  public contract record, for no gain over a member that defaults.
- **Put the relation on the request rather than the window** — a parameter to
  the renderer beside `MapLayerSource`. Rejected: the renderer's filter is
  per layer (a `useTime:false` layer, a cumulative layer), so a relation that
  rides the layer's own window is the shape the rule reads it in, and the
  identify path already carries its per-layer windows in the same record.
- **Advertise the flag per layer rather than on the root.** Rejected for now:
  `supportsTimeRelation` is a member of the map service root JSON, not a layer
  member, so moving it would be a compatibility-surface change with its own
  flag honesty to answer. The root's value is the honest summary of "some
  layer here can answer all three", and the per-layer refusal names the layer
  that cannot.
- **Refuse contains/without for the whole service when any layer is
  undesignated.** Rejected: it is a coarser answer than the request asked for,
  and it would refuse a designated layer the client named explicitly because of
  a layer it did not.
- **Serve contains/within on an undesignated layer by reading the row's dates
  as an extent anyway** (min/max over them). Rejected twice already: ADR-0175
  §Alternatives rules out the derived extent because it changes what `time`
  means on a surface already served, and ADR-0100 rules out serving the
  relation as overlaps. A designation is the opt-in that makes the question
  answerable at all.
- **Serve the relations but leave the flag `false`.** Rejected: that is the
  original defect in a new form — the engine would serve a distinction while
  telling clients not to use it, and the flag would describe nothing.

## Not decided

- **Where the designation is authored** — publish configuration, ingest
  inference, or a per-request override. Unchanged from ADR-0175: this record
  decides how a designation is *served*, not how one is supplied. No store
  sets one today, which is why no served root advertises `true` yet.
- **Whether the layer metadata should emit `timeInfo`** now that a designation
  makes it truthful. Still undecided, and still a compatibility-surface change.
- **Whether `layerTimeOptions.timeDataCumulative` should affect the extent.**
  It still does nothing, as before.

## Consequences

- `esriTimeRelationContains` and `esriTimeRelationWithin` are served as
  themselves on a designated layer, on export and on identify, and are still
  typed 400s on a layer that designates none. A client that got a confidently
  wrong picture from ADR-0058 still gets a typed refusal; a client whose layer
  designates its dates gets the distinction it asked for.
- `MapTimeExtent` gains a public member. It is additive, defaults to the
  relation the parameter's default already named, and is a core type, so the
  contract's own walls hold — but it is a contract change, which is why this
  record exists.
- The flag is now derived rather than written, so it can be wrong only if the
  designation is wrong: a misdesigned layer advertises a relation it cannot
  answer, which is a data error rather than a flag error, and is describable
  in one place per layer.
- A scheme whose layers designate nothing still advertises `false`, so the
  served flag will read `false` on most services for the foreseeable future.
  That is the honest reading rather than a gap: it is the same cost ADR-0175
  named when it chose the designation over the derived extent, and it is what
  makes the flag worth reading.
- The renderer's undesignated branch can now throw where it previously
  returned. A caller that builds a `MapLayerSource` with a contains/within
  window against an undesignated dataset now gets `invalid.arguments` instead of
  the bag rule's answer. That is the intent — the parameter is never accepted
  and ignored — but it is a new failure on a path that previously succeeded
  wrongly, and it is a public SDK surface.

## References

- ADR-0100 (`supportsTimeRelation:false`, the discarded parsed relation)
- ADR-0175 (the feature temporal extent; §3 the three relations, §6 the flag)
- ADR-0178 (`TemporalExtent`, `DatasetDescription.TimeFields`, the three readers)
- ADR-0081 (a flag is advertised exactly when the behaviour proves it)
- `src/Spatial.Contracts/RasterContracts.cs` (`MapTimeExtent.Relation`)
- `src/Spatial.Adapter.GeoServices/MapExportTime.cs`
  (`ParseTimeRelation`, `ResolveTimes`, `RequireDesignated`, `MapDesignation`)
- `src/Spatial.Adapter.GeoServices/MapExportLayers.cs` (export edge)
- `src/Spatial.Adapter.GeoServices/MapIdentifyPlan.cs` (identify edge)
- `src/Spatial.Adapter.GeoServices/FeatureSpatialMatcher.cs` (`MatchesTime`)
- `src/Spatial.Adapter.GeoServices/IdentifyFilters.cs`
- `src/Spatial.Rendering.Skia/Pipeline/FeaturePipeline.cs` (`MatchesTime`)
- `src/Spatial.Adapter.GeoServices/MapServerResources.cs` (`Root`)
- `research/compat/map-service.md` §2
- Beads: SpatialEngine-oas (the flag direction), SpatialEngine-jcu (ADR-0175),
  SpatialEngine-5ab (ADR-0178), SpatialEngine-bm7 (this record)

## Measurements

Where each half of the decision lands, and what pins it (2026-10-04):

| Concern | Site | Pinned by |
| --- | --- | --- |
| Parse | `MapExportTime.ParseTimeRelation` | `MapExportTimeTests` |
| Carry to the reader | `MapExportTime.ResolveTimes` → `MapTimeExtent.Relation` | `MapExportTimeTests` |
| Refuse without a designation | `MapExportTime.RequireDesignated`, both edges | `MapExportTimeTests`, `MapIdentifyTimeTests`, `GeoServicesMapExportTests` |
| Apply (query, identify) | `FeatureSpatialMatcher.MatchesTime` | `MapIdentifyTimeTests`, `FeatureSpatialMatcherTimeTests` |
| Apply (render) | `FeaturePipeline.MatchesTime` | `TemporalRenderTests` |
| Advertise | `MapServerResources.Root` | `MapServerRootTests`, `GeoServicesMapExportTests` |
