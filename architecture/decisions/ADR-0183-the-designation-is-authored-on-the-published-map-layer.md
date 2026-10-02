---
status: accepted
date: 2026-10-05
deciders: maintainer + agent
summary: A map layer carries its own designation of the dates bounding a feature — `timeFields` on the published layer, in configuration or over the map API — validated structurally when the map is stored and against the live schema where a declaration happens, and projected onto the dataset description at the serving edge; no store infers one and no request may supply one (amends 0175, 0182).
amends: ADR-0175, ADR-0182
related: ADR-0053, ADR-0077
consulted: ADR-0081, ADR-0100, ADR-0178
---

# ADR-0183: The designation is authored on the published map layer

## Context

ADR-0175 gave a feature a temporal extent — the interval between two date
fields the layer's schema designates — and ADR-0178 implemented the model.
ADR-0182 made the two relations that need an extent reachable over HTTP on a
layer that designates one, and moved `supportsTimeRelation` off its hard-coded
`false`. Both records are complete against `DatasetDescription.TimeFields`, and
nothing in the tree ever sets it: the only descriptions the engine builds are
the four schema-discovery constructors and the ArcGIS REST mapper, none of
which knows what a start date is. So the root reads `false` on every real
service and `contains`/`within` are typed rejects everywhere, which is the
honest answer to an empty designation set and not the feature ADR-0175
described.

ADR-0175 §"Not decided" filed the remaining question by name: where the
designation is authored — publish configuration, ingest inference, or a
per-request override. That is one decision about where a fact comes from, and
the answer decides what every other part of the path may assume. It is not a
plumbing choice: each answer makes a different party responsible for being
right about what a column means.

## Decision

**The designation is publication state: a `timeFields` member on the map
layer, supplied in declared configuration or over the map API, validated
against the live schema where it is declared, and stamped onto the dataset
description at the serving edge.**

**1. The declaration lives on `MapLayer`, not on the store or the dataset.**
`MapLayer` gains a non-positional init-only `TemporalExtentFields? TimeFields`
— the same additive shape ADR-0175 chose for the description, so no existing
construction moves and a layer that gains a designation does not become a
different record by equality. A declared map's layer carries the same pair as
`startDateField`/`endDateField` configuration members. Two absent bounds are no
designation, exactly as on the description. ADR-0053's rule is the reason: a
layer is owned by its map, so the same dataset publishes with a designation in
one map and without one in another, and neither publication mutates the store.

**2. A designation is validated twice, where each fact is knowable.** The
structural shape — identifier-shaped field names, no designation on an image
layer, nothing at all when both bounds are absent — is pure and lives in
`MapValidator`, beside the relationship validation ADR-0077 already puts there.
The live facts — each named field exists in the layer's dataset and is
date-typed — are checked by `MapTimeFieldSchemas` against the catalogue at the
moment of declaration, from the admin write route, so a designation that could
never yield an extent fails as `invalid.arguments` naming the field rather than
serving a layer whose `time` silently means something else. A non-date field is
rejected by name, because a designation over it would be accepted and ignored
at the reader, which is the failure this repository refuses in every other form.

**3. The serving edge projects the designation onto the description it reads.**
`GeoServicesResolution` stamps the published designation onto the
`DatasetDescription` it hands the root, the export/identify request edges, the
render bridge and the service query plan, in one helper every one of those
paths calls. The stamp is `description with { TimeFields = … }` applied only
when the layer declares one, so a store that ever grows its own designation
keeps it, and a layer that declares none reads the description unchanged.

**4. The render bridge carries the designation too.** `MapLayerSource` gains
the same non-positional member, and the render pipeline prefers the source's
designation over the description's. Without it the pipeline would apply the bag
rule to a designated layer and its backstop would refuse a relation the request
edge had already accepted — the export would answer a contained window with
every feature holding any instant inside it.

**5. Nothing infers a designation and no request may supply one.** Ingest
writes columns; it does not decide which of them bound a record. A
per-request override is refused by the same rule as every other ignored
parameter.

## Alternatives

- **Infer the designation at ingest from column names** (`starts`/`ends`,
  `begin`/`end`, `valid_from`/`valid_to`). Rejected: it is ADR-0175's rejected
  min/max derivation wearing a naming convention, and a wrong guess changes
  what `time` means on an already-served layer with no declaration event to
  point at. Names are not evidence in any locale or schema the engine meets,
  and the cost of the mistake is a service that answers the wrong features.
- **A store-level declaration** (a table comment, a per-dataset registry row).
  Rejected: it makes the same dataset's `time` mean different things in
  different services, and it couples a schema change to every service that
  publishes the table — the publication is where the operator already declares
  what a layer *is*.
- **A per-request override** (`timeStartField`/`timeEndField` parameters).
  Rejected: it hands a client the power to redefine the meaning of `time` on a
  served layer, so two requests over one service disagree about the same
  rows, and it would make `supportsTimeRelation` a statement about a parameter
  rather than about the service.
- **A dedicated admin resource for designations** (PUT `/api/datasets/{id}/time`).
  Rejected: it invents a second place to declare one thing about a layer, with
  its own validation and its own lifecycle, when the map already owns the layer.
- **Serve contains/within by inferring an instant extent on an undesignated
  layer.** Rejected: ADR-0175 filed it as "not decided" precisely because it is
  a guess about which layers clients mean; this record does not need it,
  because an operator can now designate the layer instead.

## Not decided

- **Whether a designation emits `timeInfo` in the layer metadata.** ADR-0175
  left it open; the designation makes the field truthful, and emitting it is a
  compatibility-surface change with its own flag honesty to answer. Unchanged
  by this record: the layer metadata reads exactly as it does today.
- **Whether `layerTimeOptions.timeDataCumulative` should affect the extent.**
  Still nothing behind it, as ADR-0175 found.
- **An undesignated layer with exactly one date field** read as an instant
  extent. Still a guess about which layers clients mean; the designation is the
  answer for the layers an operator cares about, so the guess has less to do.

## Consequences

- The relations ADR-0100 refused are reachable: a publication that designates
  its layers advertises `supportsTimeRelation: true` and serves
  `contains`/`within` against real extents. The cost is that an operator must
  now say what a layer's dates mean, and a layer that has not been told keeps
  ADR-0100's flag and refusals.
- A designation is a behaviour change for its layer's `time` queries —
  overlaps stops meaning "any instant inside" and starts meaning "the extent
  intersects" — so it is authored, validated and describable rather than
  inferred, and the mistake is one map edit away from being corrected.
- `GET /api/datasets/{id}` is unchanged: the description a dataset reports is
  the store's, and the designation is publication state the serving edge adds.
  Undesignated layers stay byte-identical to today on every served surface.
- Declared (configuration-seeded) maps are validated structurally but not
  against live schemas, because they are seeded without opening a store — the
  same division ADR-0077 already has for relationships.

## References

- `architecture/decisions/ADR-0175-a-features-temporal-extent-is-what-the-layer-designates.md`
  (the model, and the question this record answers),
  `ADR-0182-serve-the-contains-and-within-relations-on-a-designated-layer.md`
  (the surface), `ADR-0053`, `ADR-0077` (the two-tier validation this copies).
- Files: `src/Spatial.Contracts/Providers/IMapRegistry.cs` (`MapLayer.TimeFields`),
  `src/Spatial.Contracts/RasterContracts.cs` (`MapLayerSource.TimeFields`),
  `src/Spatial.Maps/MapValidator.cs`, `src/Spatial.Maps/MapTimeFieldSchemas.cs`
  (new), `src/Spatial.Maps/MapRegistry.cs` and `MapsOptions.cs` (declared config),
  `src/Spatial.Host/Api/AdminEndpoints.cs` (declaration-time validation),
  `src/Spatial.Adapter.GeoServices/GeoServicesResolution.cs`,
  `MapServerResources.cs`, `MapExportLayers.cs`, `MapRenderEngine.cs`,
  `ServiceQueryPlan.cs`, `src/Spatial.Rendering.Skia/Pipeline/FeaturePipeline.cs`.
- Bead: SpatialEngine-0ym, from SpatialEngine-bm7.
