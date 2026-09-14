---
status: accepted
date: 2026-09-14
deciders: maintainer + agent
---

# ADR-0060: Feature write-model extensions (service query, generateRenderer reuse, validateSQL, aggregation honesty, attachments)

## Context

The compatibility matrix (`research/compat/feature-service.md` §1,
follow-up T-C, task T-038) records five write-model gaps against the live
S1/S4 (Feature Service) reference:

1. Service-level `FeatureServer/query` (S1 `query-feature-service/`):
   only layer-level `.../<id>/query` is served
   (`GeoServicesEndpoints.cs`).
2. `generateRenderer` (S4): no feature-service route; renderers are
   persisted-style projections (ADR-0050).
3. `validateSQL` (S4): the closed grammar rejects unknown syntax at query
   time instead of validating server-side.
4. `queryBins` / `queryTopFeatures` / `queryAnalytic` (S4): the engine
   serves `outStatistics` but not the bin/top-features/analytic shapes.
5. Attachments (`queryAttachments`, `addAttachment`,
   `deleteAttachments`, `updateAttachment`): no attachment store or route.

Two constraints pin the answer (AGENTS.md hard walls):

- **ADR-0001 / principle 1:** spatial algorithms live in implementation
  projects; `Spatial.Core` stays structural. Everything below is adapter
  surface over already-materialised matches — no Core change.
- **ADR-0005 / principle 8:** public contracts carry only core types;
  `Spatial.PluginSdk` references only `Spatial.Core` and takes no packages.
  No SDK change ships here; the attachment store-model decision is recorded
  for a follow-up track, not implemented.

A landscape change since T-038 was written: per-layer `generateRenderer`
for **map-service** layers landed via T-039 as
`MapGenerateRenderer.cs` (ADR-0055), whose docstring names T-038 as the
required reuser. Item 2 is therefore reuse-or-close, not a second
classifier.

## Decision

**Serve the service-level query, the feature-service `generateRenderer`
route (reusing the T-039 classifier), and `validateSQL`; mount honest typed
rejects for the aggregation extensions and the attachment writes with empty
reads; record the attachment store-model decision here and spawn
implementation sub-tasks. No new SDK capability, no new package, no Core
change.**

### 1. Service-level `FeatureServer/query` (S1)

`GET/POST /{service}/FeatureServer/query` fans the shared query subset out
across the service's layers **and** tables and returns the S1 response:
`{"layers": [{id, objectIdFieldName, globalIdFieldName: "",
geometryType?, spatialReference?, fields, features,
"exceededTransferLimit"}, ...]}` for the full shape, `[{id, count}]` for
`returnCountOnly`, `[{id, objectIdFieldName, objectIds}]` for
`returnIdsOnly`. The layer-only keys (`geometryType`,
`spatialReference`) are omitted for tables, exactly as the layer query
shapes they mirror.

- Shared parameters parse with the unchanged `EsriFeatureQuery.Parse`
  (same subset, same rejects); paging, ordering, `outSR`,
  `returnExceededLimitFeatures` and `maxRecordCountFactor` apply **per
  layer**.
- `layerDefs` is parsed in all three S1 syntaxes (simple
  `0:where;5:where`, JSON object, JSON array with per-layer `outFields`).
  When present, only the named layers are queried; unknown ids are typed
  `not.found`, never silently dropped. A layer definition expression and
  the shared `where` both apply (conjoined via a new
  `EsriFilterClause.And` combinator — no re-parsing of rendered text), and
  a definition expression naming a field its layer does not carry fails
  the request, the same strictness as a layer query.
- Layer-level result shapes (`returnExtentOnly`,
  `returnDistinctValues`, `outStatistics`, `uniqueIds` /
  `returnUniqueIdsOnly`) are typed `invalid.arguments` naming the
  `.../<layerId>/query` route: they are per-layer responses and stay
  there. A geometry without a spatial reference is read in the first
  queried layer's CRS and reprojected per layer by the existing
  `TransformQueryGeometry` path.
- Execution lives in `FeatureQueryEngine.ServiceQueryAsync` (the query
  path's fan-out stays cohesive, ADR-0040); `layerDefs` parsing lives in
  `FeatureServiceQuery`.

### 2. `generateRenderer`: served by T-039 reuse

`GET/POST
/{service}/FeatureServer/{layerId}/generateRenderer` delegates to the
single `MapGenerateRenderer.GenerateAsync` classifier (equal-interval
`classBreaksDef`, single-field `uniqueValueDef`, same typed rejects).
The T-038/Test track proves the reuse with an integration test asserting
byte-identical `renderer` payloads on the FeatureServer and MapServer
surfaces for the same classification — item 2 closes as served-by-T-039
with a feature-service route, no duplicated classifier.

### 3. `validateSQL` (S4)

`GET/POST /{service}/FeatureServer/{layerId}/validateSQL` validates the
required `sql` parameter and returns the S4 shape:
`{"isValidSQL": true}`, or `{"isValidSQL": false, "validationErrors":
[{errorCode, description}]}` with the spec codes 3001 (not supported),
3002 (syntax error) and 3008 (invalid field name). `sqlType` defaults to
`where`; `expression` / `statement` validate as 3001 (no engine evaluation
model — validated, never run, so client text still never becomes SQL
structure). Semantic checking needs the clause's field references, so
`EsriFilterClause` gains `ReferencedFields` (comparison and `IS NULL`
operands, constants reference none); a name validates when the schema
carries it (the exact case-sensitive match the query path applies) or it
is the synthetic `OBJECTID` (ADR-0037). A missing `sql` or an unknown
`sqlType` is a 400 invalid-argument failure like every other required
parameter.

### 4. Aggregation extensions: honest rejects, unadvertised

`queryBins`, `queryTopFeatures` and `queryAnalytic` have no engine model.
They are mounted at layer level so clients get a typed
`invalid.arguments` failure naming the served alternative
(`outStatistics` + `groupByFieldsForStatistics` for bins/analytic;
`orderByFields` + `resultRecordCount` for top-features) instead of a bare
404. Unknown layer ids stay `not.found`. Nothing is advertised: no
capability flags name these operations.

### 5. Attachments: store-model decision (no store this track)

No dataset carries attachment blobs and no layer advertises
`hasAttachments` (verified by test: the metadata carries neither
`hasAttachments` nor `attachmentProperties`). Until an attachment store
exists:

- `.../{layerId}/queryAttachments` and
  `.../{layerId}/{objectId}/attachments` report the truthful empty set
  (`{"attachmentInfos": []}` — nothing is stored for any feature);
- `addAttachment` / `deleteAttachments` / `updateAttachment` (POST-only,
  per spec) fail as typed `invalid.arguments` naming the missing
  capability; unknown layers stay `not.found`.

**Store-model decision:** attachments need a new additive SDK capability
(an `IFeatureAttachmentStore` alongside `IFeatureEditStore`, ADR-0037
pattern: per-feature blob put/get/delete keyed by layer dataset and
object id, provider-owned bytes, core-typed descriptors on the contract),
plus a persistence decision per provider (PostGIS bytea/large-object vs
sidecar object store) and a quota/auth story for the single-admin-token
model. That is a multi-provider track, not this one: implementation lands
as `eng/tasks` sub-tasks depending on T-038, and this surface stays
honest and unadvertised until one lands.

## Consequences

- New adapter surface only: `FeatureServiceQuery.cs`,
  `FeatureValidateSql.cs`, `FeatureAttachments.cs`,
  `GeoServicesEndpoints.FeatureOps.cs`, `ServiceQueryAsync` on
  `FeatureQueryEngine`; `EsriFilterClause.And` / `ReferencedFields` in
  `Spatial.Interop.Esri` (shared codec, no new packages).
- Behaviour changes land with contract (unchanged — adapter-only),
  test (unit + HTTP red-first) and doc updates
  (`geoservices-compatibility.md` §3) together, with success, failure
  and cancellation covered.
- The 0059 collision between parallel tracks (map-offline vs
  image-capability) is out of scope; this record takes the next free
  number verified across all worktrees (0060).
