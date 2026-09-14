# Feature Service compatibility matrix

Sources (checked 2026-09-14):
- S1 Feature Service: https://developers.arcgis.com/rest/services-reference/enterprise/feature-service/
- S2 Layer (Feature Service): https://developers.arcgis.com/rest/services-reference/enterprise/layer-feature-service/
- S3 Query (Feature Service layer): https://developers.arcgis.com/rest/services-reference/enterprise/query-feature-service-layer/
- S4 Child-op link graph on S1 (~60 `.../enterprise/<op>/` pages: `add-features/`,
  `update-features/`, `delete-features/`, `apply-edits-feature-service-layer/`,
  `query-related-records-feature-service/`, `query-attachments-feature-service-layer/`,
  `generate-renderer/`, `validate-sql-feature-service-layer/`, `query-bins/`,
  `query-top-features-feature-servicelayer/`, `query-analytic/`, replica/sync pages…)
- G1 Ground truth: `ground-truth/feature-root.DamageAssessment.json` (full key list,
  10.91), `ground-truth/feature-layer0.DamageAssessment.json`,
  `ground-truth/feature-query-count.json|ids|extent`

Our surface: `src/Spatial.Adapter.GeoServices/GeoServicesEndpoints.cs`
(routes), `FeatureService.cs`, `EsriFeatureQuery.cs` (params),
`FeatureQueryEngine.cs` (execution), `EsriLayerModel.cs` (metadata).

## 1. Resources & operations

| ArcGIS capability | Ours | Status | Evidence |
|---|---|---|---|
| Service root (`FeatureServer?f=json`: `layers[]`, `tables[]`, `capabilities`, `maxRecordCount`, `supportedQueryFormats`, `units`, extents) | served | **Have** | `GeoServicesEndpoints.cs`, `FeatureService.cs:Root`; tables served (T-014) |
| `FeatureServer/<layerId>` metadata (fields, `geometryType`, `objectIdField`, `drawingInfo`, `templates`, `capabilities`, `timeInfo`, `indexes`) | served | **Have** | `EsriLayerModel.cs`; drawingInfo/labeling/domains projected (ADR-0050) |
| `.../<layerId>/query` core: `where`, `objectIds`, envelope+`Intersects`, `outFields`, `returnGeometry`, `outSR`, `orderByFields`, paging, ids/count/extent-only, distinct, statistics+`groupBy`+`having`, `time`, date literals | served | **Have** | `EsriFeatureQuery.cs:56-105`; `FeatureQueryEngine.cs` |
| `inSR` honouring | served | **Have** | `EsriFeatureQuery.cs:78,86` (T-020) |
| `returnExceededLimitFeatures`, `maxRecordCountFactor` | served | **Have** | `EsriFeatureQuery.cs:101-102` (T-021) |
| `geometryPrecision`, `maxAllowableOffset` | served | **Have** | `EsriFeatureQuery.cs:81-82` (T-023) |
| 7 spatial relations (all but `IndexIntersects`) | served | **Have** | `EsriFeatureQuery.cs:ParseSpatialRel` (T-023) |
| `advancedQueryCapabilities` + `supportsStatistics/AdvancedQueries/supportedQueryFormats` truthfully | served | **Have** | `EsriLayerModel.cs` (T-019) |
| `f=pjson` alias; `f=geojson` honest reject naming `supportedQueryFormats` | served | **Have** | `EsriJson.cs:40-55` |
| `addFeatures` / `updateFeatures` / `deleteFeatures` / `applyEdits` (layer level, gated on `IFeatureEditStore` + integer identity; `rollbackOnFailure` → `ITransactionStore`) | served | **Have** | `FeatureEditEngine.cs`; `GeoServicesEndpoints.cs:58-67` (ADR-0037) |
| Feature (object) resource `.../<layerId>/<objectId>` | served | **Have** | `GeoServicesEndpoints.cs` (REST-JS `getFeature` proof) |
| Service-level `/query` (query across all layers) | — | **Missing** | S1 lists `query-feature-service/`; we only serve layer-level `.../<id>/query` |
| `generateRenderer` (classBreaks/uniqueValue server-side classification) | — | **Missing** | S4 `generate-renderer/`; no route; our renderers are client-persisted style projections (ADR-0050) |
| `queryRelatedRecords` (relationship traversal) | — | **Missing** | S4; no relationship model in engine (recorded non-goal in `geoservices-compatibility.md` §7.1) |
| Attachments (`attachmentInfos`, `addAttachment`, `deleteAttachments`, `updateAttachment`) | — | **Missing** | S4 `add-attachment/` etc.; no attachment store or route |
| `htmlPopup`, layer `image` resource | — | **Missing** | non-goal §7.1 (no engine model) |
| `queryBins` / `queryTopFeatures` / `queryAnalytic` (aggregation/binning extensions) | — | **Missing** | S4; engine serves `outStatistics` but not bin/top-features/analytic shapes |
| `validateSQL` (server-side WHERE validation) | — | **Missing** | S4; our closed grammar rejects unknown syntax at query time instead |
| Replica / sync (`createReplica`, `synchronizeReplica`, `extractChanges`, `replicas`) | — | **Non-goal** | S4 sync pages; no versioned-geodatabase model (`gdbVersion`/`historicMoment` rejected by design) |
| Contingent values / field groups / shared templates / data-element queries (`queryContingentValues`, `queryDataElements`, `queryDomains`, `querySharedTemplates`, subtypes `types[]` beyond basic) | — | **Partial** | live root advertises `supportsQueryContingentValues/supportsQueryDataElements/supportsQueryDomains`; we serve domains only (ADR-0050). Ground truth `feature-root` lists all three flags |
| Full-text `text` search (+ `supportsFullTextSearch`, searchable-fields list) | honestly rejected | **Partial** | `EsriFeatureQuery.cs:RejectUnsupported` (`text` → 400 naming `where…LIKE`); caps flags not advertised |
| `returnEnvelope` (envelopes instead of geometries, 11.4+) | — | **Missing** | S3 "New at 11.4"; no `parameters.Get("returnEnvelope")` in tree |
| `resultPaginationToken` workflow (keyset paging, S3) | — | **Missing** | S3 section; we page only by `resultOffset`/`resultRecordCount` |
| `defaultSR` (request-wide spatial reference shorthand, 11.3+) | — | **Missing** | S3 "New at 11.3"; never read |
| `uniqueIds` / `returnUniqueIdsOnly` (string-ID databases, 11.5+) | — | **Missing** | S3 "New at 11.5"; our `EsriObjectIdScheme` is integer-identity only |
| Percentile statistic type + `supportsCountDistinct/supportsPercentileStatistics/supportsExceedsLimitStatistics` flags | — | **Partial** | S3; we serve count/sum/min/max/avg/stddev/var (`EsriFeatureQuery.cs`) but not `percentile_*`; live layer lists the flags, we don't emit them |
| 64-bit objectIds / high-precision dates / time-only/date-only/timestamp-offset/big-integer field types (11.2–11.3) | — | **Partial** | S2 examples 16–17; our `AttributeKind` has no time-only/date-only/bigint faces |
| `datumTransformation`, `defaultSR`-style WKT2 spatial references | honestly rejected | **Partial** | rejected by name; WKT2 SR input not accepted (`EsriValueParser.ParseSpatialReference`) |
| `distance`+`units` (query-with-distance), `returnCentroid`, `multipatchOption`, `returnTrueCurves`, `resultType`, `sqlFormat`, `quantizationParameters`, `relationParam` | honestly rejected | **Non-goal** | each rejected by name (`EsriFeatureQuery.cs:465-498`); pinned by §7.1 non-goals |
| `returnZ`/`returnM` | honestly rejected | **Non-goal** | codec serves 2D explicitly |

## 2. Response-shape deltas vs ground truth (same-data proof)

Replay `ground-truth/feature-layer0.*` against our layer metadata: live layer
carries ~80 keys (full list §G1 file). Deltas that change client branching:
`advancedQueryAnalyticCapabilities`, `supportsQuantizationEditMode`,
`supportsExceedsLimitStatistics`, `supportsCountDistinct`,
`supportsPercentileStatistics`, `supportsDefaultSR`, `supportsFullTextSearch`
absent on ours; `attachmentProperties/attachmentFields` absent (no attachments);
`types[]` (subtypes) minimal; `editFieldsInfo`/`ownershipBasedAccessControl`
absent (single-admin-token model, not per-user editing). Everything a
read/query client branches on (`advancedQueryCapabilities`, `supportsStatistics`,
`supportedQueryFormats`, `maxRecordCount`, `objectIdField`, `fields`,
`geometryType`, `extent`) is present and replay-tested.

## 3. Follow-ups (filed)

- T-A Feature modern query params: `returnEnvelope`, `resultPaginationToken`, `defaultSR`, `uniqueIds`/`returnUniqueIdsOnly`.
- T-B Statistics/capability honesty: percentile stats + `supportsCountDistinct/supportsPercentileStatistics/supportsExceedsLimitStatistics/supportsDefaultSR/supportsFullTextSearch` flags pinned by live replay.
- T-C Feature write-model extensions: attachments, `generateRenderer`, `validateSQL`, `queryBins`/`queryTopFeatures`, service-level `/query` (one scoping task; attachments sub-tasks spawn on claim).
