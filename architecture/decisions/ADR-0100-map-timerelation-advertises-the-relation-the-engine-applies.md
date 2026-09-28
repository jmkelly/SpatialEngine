---
status: accepted
date: 2026-09-28
deciders: maintainer + agent
amends: ADR-0058
---

# ADR-0100: The map root advertises the time relation the engine actually applies

## Context

ADR-0058 closed the MapServer export time gap (T-040) by serving `time` and
`layerTimeOptions`, and claimed in the same breath that
`timeRelation` "accepts the documented relations
(`esriTimeRelationOverlaps` default, `Contains`, `Within`) — the engine's date
values are instants, so all three reduce to containment" while "the root
advertises `supportsTimeRelation: true`".

The claim did not survive contact with the code:

- `MapExportTime.ParseTimeRelation` returned the parsed relation and both
  call sites discarded it — `MapExportLayers.cs:38` on the export path and
  `MapIdentifyPlan.cs:31` on the identify path (`_ = …`). The resolved window
  is always the one `time` produces, and the temporal rule applied to a
  feature is always "any date value inside that window"
  (`FeaturePipeline.MatchesTime`, `FeatureSpatialMatcher.MatchesTime`).
- So `esriTimeRelationContains` and `esriTimeRelationWithin` were **accepted
  and served as overlaps**, on both paths, silently.
- The root advertised `supportsTimeRelation: true`
  (`MapServerResources.cs:91`), so a client that branched on it — which is
  exactly what the flag is for — was promised a distinction the engine does
  not make.

`research/compat/map-service.md` §2 (refreshed under SpatialEngine-itu) states
the gap rather than the surface. Two routes close it: apply the relation, or
stop advertising it.

Applying the relation is not a parameter change. Esri's `contains`/`within`
compare the requested window against **the feature's time extent** — a start
and an end. The engine has no such model: a feature carries a bag of
`DateTimeOffset` attribute values, and the shared temporal rule in all three
readers (query, export render, identify) is "any of them inside the window,
dateless features pass". Making the three relations distinguishable means
deciding what a feature's time extent *is* — min/max over all date attributes,
a designated start/end pair, something the store declares — and changing a
rule that lives in the render pipeline, the identify matcher and the query
path at once. That is a model decision about temporal data, well past
"apply the relation to the resolved time window", and overturns ADR-0058's
premise that the three relations reduce to one.

## Decision

**The root advertises the relation the engine applies, and every other
relation is rejected by name on both paths.**

1. **`supportsTimeRelation: false` on the map root.** The field stays — it is
   part of the Esri map service JSON and every real service carries it (the
   G1 roots in `research/compat/ground-truth/` both emit it) — and its value
   is now the truth: the engine applies one relation, and the parameter's
   other values are refused. This is ADR-0081's rule applied to the map
   surface rather than the feature layer: a flag is advertised exactly when
   behaviour proves it, and a capability the facade does not earn is stated
   as `false` rather than claimed.
2. **`MapExportTime.ParseTimeRelation` accepts blank or
   `esriTimeRelationOverlaps` (case-insensitive, trimmed) and rejects
   everything else** with the usual typed `invalid.arguments`, naming the
   relation the engine does apply and the flag that says so. This is the same
   rule ADR-0058 already applied to a non-zero `timeOffset` and to unsupported
   `dynamicLayers` data sources: an unsupported construct fails loudly rather
   than being accepted and ignored. `esriTimeRelationOverlaps` stays
   accepted because it names the behaviour that is served, and mainstream
   clients send it unprompted.
3. **Both call sites keep the parse and now mean it** — the export path
   (`MapExportLayers`) and the identify path (`MapIdentifyPlan`) validate the
   parameter, and neither discards a value any more. `Spatial.Contracts`,
   the render contract (`MapTimeExtent`), the SDKs and the workbench are
   untouched: nothing about the served window changes, only what the engine
   claims about it and what it refuses.

## Consequences

- A client that branches on `supportsTimeRelation` now sends the overlaps
  relation, or none, and gets the window it asked for. A client that sends
  `esriTimeRelationContains` gets a typed 400 naming the applied relation
  instead of a confidently wrong picture.
- `esriTimeRelationContains`/`Within` on a temporal service are a breaking
  change for any caller that relied on them being quietly accepted. They were
  never honoured, so no correct behaviour regresses — only a wrong answer
  stops being returned.
- The temporal rule stays in one place with one meaning. Nothing in
  `Spatial.Core`, the render contract or the query path moves.
- Serving the other relations is now visibly missing rather than invisibly
  wrong, which is what makes it a decidable piece of work: it needs a feature
  temporal-extent model, and the model is the decision to take, not the flag.

## Alternatives

- **Apply the relation to the resolved window** (the other route this
  decision was offered). Rejected here, not in principle: it requires the
  feature temporal extent that the engine does not model, so it is a temporal
  data model decision rather than a parameter change, and it would change the
  shared temporal rule in the renderer, the identify matcher and the query
  path together. Filed as its own follow-up rather than smuggled in here.
- **Drop the `timeRelation` parameter entirely and reject every value,
  including `esriTimeRelationOverlaps`**: the most literal reading of
  `supportsTimeRelation: false`. Rejected because it breaks mainstream
  clients that always send the default relation for a value the engine does
  serve, buying no honesty the flag does not already carry.
- **Keep `true` and document the reduction** (the status quo). Rejected: the
  flag's whole use is that a client branches on it, and a branch that leads to
  a silently widened temporal query is the failure the repo's standing rule
  (T-024 audit, ADR-0058's own alternatives) exists to prevent.
- **Omit the field instead of emitting `false`**: ADR-0081's rule for the
  feature-layer flag family. Not applicable — unlike those flags, this one is
  a member of the map service JSON schema that clients read positionally, and
  every ground-truth root carries it. `false` is the truthful value of a field
  that is present, not a missing claim.

## References

- ADR-0058 (map export time, dynamicLayers, layerOption, cached-root honesty)
- ADR-0081 (advertise the capability flags the served query surface earns)
- ADR-0060 (honest rejects by name)
- `research/compat/map-service.md` §§1–2
- `research/compat/ground-truth/map-root.Census.json`,
  `map-root-cached.WorldTopo.json`
