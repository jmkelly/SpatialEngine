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
(routes), `FeatureWriteModelEndpoints.cs` (service-level `query`, the per-layer
`generateRenderer`/`validateSQL`/aggregation rejects, the attachment surface),
`FeatureAttachmentHandlers.cs`/`FeatureAttachmentWrites.cs`,
`FeatureAttachments.cs` (the S4 response shapes), `FeatureValidateSql.cs`,
`MapGenerateRenderer.cs` (the shared classifier this surface reuses),
`FeatureService.cs`, `EsriFeatureQuery.cs` (params),
`FeatureQueryEngine.cs` (execution, `ServiceQueryAsync`),
`EsriLayerModel.cs` (metadata).

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
| `supportsQuantization` (layer root + `advancedQueryCapabilities`) and `supportsPaginationOnAggregatedQueries` | served | **Have** | `EsriLayerModel.cs` (ADR-0081); quantization proved by ADR-0079, aggregated paging by `FeatureStatisticsEngine` |
| `f=pjson` alias; `f=geojson` honest reject naming `supportedQueryFormats` | served | **Have** | `EsriJson.cs:40-55` |
| `addFeatures` / `updateFeatures` / `deleteFeatures` / `applyEdits` (layer level, gated on `IFeatureEditStore` + integer identity; `rollbackOnFailure` → `ITransactionStore`) | served | **Have** | `FeatureEditEngine.cs`; `GeoServicesEndpoints.cs:58-67` (ADR-0037) |
| Feature (object) resource `.../<layerId>/<objectId>` | served | **Have** | `GeoServicesEndpoints.cs` (REST-JS `getFeature` proof) |
| Service-level `/query` (query across all layers) | served | **Have** | `FeatureWriteModelEndpoints.cs` (`/{service}/FeatureServer/query`) → `FeatureQueryEngine.ServiceQueryAsync`; the shared `EsriFeatureQuery` subset plus `layerDefs` in all three S1 syntaxes, one feature set, count or id list per layer. The layer-only shapes (`returnExtentOnly`, `returnDistinctValues`, `outStatistics`, `uniqueIds`/`returnUniqueIdsOnly`) are typed `invalid.arguments` naming `.../<layerId>/query` — they are per-layer responses (ADR-0061 §1) |
| `generateRenderer` (classBreaks/uniqueValue server-side classification) | served | **Have** | the single `MapGenerateRenderer` classifier the MapServer already serves, reused rather than duplicated; `GeoServicesFeatureOpsTests` pins the same rendered payload on both surfaces (ADR-0055, ADR-0061 §2) |
| `queryRelatedRecords` (relationship traversal) | served | **Have** | `FeatureRelationshipEngine.cs` over map-declared relationships (ADR-0077); `relationships` metadata on the layer, related layer's own `where`/`outFields`, `relate`/`unrelate` behind the edit gate |
| Attachments (`attachmentInfos`, `addAttachment`, `deleteAttachments`, `updateAttachment`) | served on the `IFeatureAttachmentStore` face | **Have** | `FeatureWriteModelEndpoints.cs`, `FeatureAttachmentHandlers.cs`: `queryAttachments`, the per-feature `attachments` resource and a per-attachment content resource are public reads; `addAttachment`/`updateAttachment` (multipart) and `deleteAttachments` (per-id results) require the single admin token; a layer on a capable store advertises `hasAttachments` + `attachmentProperties` (ADR-0065, ADR-0066). The memory, SQL Server and PostGIS providers all expose the face (ADR-0065 §2, ADR-0073); a store that does not (demo, ArcGIS REST) keeps the honest surface: `hasAttachments:false`, empty reads, typed write rejects naming the missing face |
| `htmlPopup`, layer `image` resource | — | **Missing** | non-goal §7.1 (no engine model), and no route on this surface: the MapServer picture-symbol image resource is a typed `not.found` (ADR-0050) and the FeatureServer mounts no `images/{imageId}` at all. Layer metadata stays honest — it reports `esriServerHTMLPopupTypeNone` on the map surface and never claims a popup model |
| `queryBins` / `queryTopFeatures` / `queryAnalytic` (aggregation/binning extensions) | mounted, honestly rejected | **Non-goal** | no engine model, so each is mounted and fails typed `invalid.arguments` naming the served alternative (`outStatistics` + `groupByFieldsForStatistics` for bins/analytic, `orderByFields` + `resultRecordCount` for top-features) instead of a bare 404; nothing advertises them (ADR-0061 §4) |
| `validateSQL` (server-side WHERE validation) | served | **Have** | `FeatureValidateSql.cs`: the S4 `isValidSQL` shape with codes 3001/3002/3008, field references checked against the layer schema (plus the synthetic `OBJECTID`), `expression`/`statement` validated as not-supported and never run, so client text never becomes SQL structure (ADR-0061 §3) |
| Replica / sync (`createReplica`, `synchronizeReplica`, `extractChanges`, `replicas`) | — | **Non-goal** | S4 sync pages; no versioned-geodatabase model (`gdbVersion`/`historicMoment` rejected by design) |
| Contingent values / field groups / shared templates / data-element queries (`queryContingentValues`, `queryDataElements`, `querySharedTemplates`, subtypes `types[]` beyond basic) | — | **Partial** | ground truth `feature-root` advertises `supportsQueryContingentValues/supportsQueryDataElements/supportsQueryDomains`; we serve domains only (ADR-0050), and the domain query operation itself is served on the MapServer surface (ADR-0055), not here |
| Full-text `text` search (+ `supportsFullTextSearch`, searchable-fields list) | honestly rejected | **Have** | `EsriFeatureQuery.cs:RejectUnsupported` (`text` → 400 naming `where…LIKE`); the layer advertises `supportsFullTextSearch: false` with an empty searchable-fields list (ADR-0057) |
| `returnEnvelope` (envelopes instead of geometries, 11.4+) | served | **Have** | `EsriFeatureQuery.ReturnEnvelope` (`EsriFeatureQuery.cs:134`) → `EsriFeatureWriteOptions.ReturnEnvelope`: every geometry the response (or the single-feature resource) writes becomes its `{xmin,ymin,xmax,ymax}` envelope in the feature's own, possibly `outSR`-reprojected CRS; a feature with no geometry writes `null` (ADR-0056 §1) |
| `resultPaginationToken` workflow (keyset paging, S3) | served | **Have** | opaque base64url `{v:1, offset}` tokens over the already-materialised ordered match set, returned beside `exceededTransferLimit`; token + `resultOffset`, token + an unpaged shape, and a forged or version-mismatched token are typed failures (ADR-0056 §3) |
| `defaultSR` (request-wide spatial reference shorthand, 11.3+) | served | **Have** | the fallback behind the explicit params on both sides of the request — in: the query geometry's own SR, then `inSR`, then `defaultSR`, then the layer SR; out: `outSR`, `defaultSR`, then the layer SR. Advertised as `supportsDefaultSR` (ADR-0056 §2, ADR-0057) |
| `uniqueIds` / `returnUniqueIdsOnly` (string-ID databases, 11.5+) | served | **Have** | a dataset with exactly one `String` identity column serves both, and the layer advertises the field as `uniqueIdField` (ADR-0056 §4); a `Guid` identity serves the canonical `D` form (ADR-0067). Integer-identity and ordinal layers keep the honest reject-by-name pointing at `objectIds` |
| Percentile statistic type + `supportsCountDistinct/supportsPercentileStatistics/supportsExceedsLimitStatistics/supportsDefaultSR/supportsFullTextSearch` flags | served | **Have** | `percentile_cont`/`percentile_disc` + COUNT DISTINCT served; flags advertised with proved values, `supportsFullTextSearch` false with an empty searchable list (ADR-0057) |
| 64-bit objectIds / high-precision dates / time-only/date-only/timestamp-offset/big-integer field types (11.2–11.3) | — | **Partial** | S2 examples 16–17; our `AttributeKind` has no time-only/date-only/bigint faces |
| `datumTransformation`, `defaultSR`-style WKT2 spatial references | honestly rejected | **Partial** | `datumTransformation` rejected by name; WKT2 SR input not accepted — `EsriValueParser.ParseSpatialReference` takes a WKID or `{wkid}` and nothing else. The internal WKT catalogue and datum-transformation graph (ADR-0086, ADR-0087) are engine-internal and are not a wire format for `inSR`/`outSR` |
| `distance`+`units` (query-with-distance) | served | **Have** | buffered band in the layer CRS, shared curated unit table (ADR-0085) |
| `returnCentroid` | served | **Have** | `IGeometryMeasures.Centroid` written beside the geometry (ADR-0085) |
| `returnZ`/`returnM` | served | **Have** | ordinate selection + the `hasZ`/`hasM` flags the Esri arrays need (ADR-0085) |
| `quantizationParameters` | served | **Have** | view-grid quantization of x/y/z/m plus a bounded generalization (ADR-0079); an unservable `mode`/`originPosition` is rejected by name; the layer advertises `supportsQuantization` so the REST JS gate opens (ADR-0081) |
| `multipatchOption`, `returnTrueCurves`, `resultType`, `sqlFormat`, `relationParam` | honestly rejected | **Non-goal** | each rejected by name (`EsriFeatureQuery.cs:RejectUnsupported`); pinned by §7.1 non-goals |
| `hasZ`/`hasM` on the layer resource | served | **Have** | `EsriLayerModel.cs` (ADR-0084); derived from `DatasetDescription.GeometryLayout`, which PostGIS fills from the geometry column's declared type modifier and the ArcGIS REST store from the remote layer's own `hasZ`/`hasM` (ADR-0091) — proved true by the Z/M/ZM column tests, and proved *absent* for a 2D and an unconstrained column |

## 2. Response-shape deltas vs ground truth (same-data proof)

Replay `ground-truth/feature-layer0.*` against our layer metadata: live layer
carries ~80 keys (full list §G1 file). Deltas that change client branching:
`advancedQueryAnalyticCapabilities`, `advancedEditingCapabilities`,
`editFieldsInfo`, `ownershipBasedAccessControlForFeatures`,
`supportsQuantizationEditMode` (we quantize on the query path only),
`supportsValidateSQL`, `supportsCalculate`,
`supportsRollbackOnFailureParameter`, `attachmentFields` and
`supportsAttachmentsByUploadId` (no upload-id workflow and no physical
attachment table, ADR-0066 §Consequences), `types[]` (subtypes) minimal, and
`hasZ`/`hasM` on a
two-dimensional layer (we describe the ordinates the store proves and omit the
rest, ADR-0084) — all absent, because the
facade does not serve the behaviour they name. `attachmentProperties` is
absent only on a store with no attachment face: a layer whose store
exposes `IFeatureAttachmentStore` advertises `hasAttachments: true` with the
descriptor faces it serves (ADR-0066 §1). Everything a read/query client
branches on (`advancedQueryCapabilities`, `supportsStatistics`,
`supportsQuantization`, `supportsDefaultSR`, `supportsExceedsLimitStatistics`,
`supportsCountDistinct`, `supportsPercentileStatistics`, `supportedQueryFormats`,
`maxRecordCount`, `objectIdField`, `fields`, `geometryType`, `extent`,
`uniqueIdField`, `hasZ`/`hasM` on a dataset that carries them) is
present and replay-tested. A flag the facade does not earn is omitted rather
than emitted as `false` (ADR-0081).

## 3. Follow-ups (filed)

- T-A Feature modern query params: `returnEnvelope`, `resultPaginationToken`, `defaultSR`, `uniqueIds`/`returnUniqueIdsOnly`. **Done (ADR-0056; guid identity ADR-0067; the `uniqueIdField` advertisement that ADR-0056 deferred is now on the layer).**
- T-B Statistics/capability honesty: percentile stats + `supportsCountDistinct/supportsPercentileStatistics/supportsExceedsLimitStatistics/supportsDefaultSR/supportsFullTextSearch` flags pinned by live replay. **Done (ADR-0057, ADR-0081).**
- T-C Feature write-model extensions: attachments, `generateRenderer`, `validateSQL`, `queryBins`/`queryTopFeatures`, service-level `/query` (one scoping task; attachments sub-tasks spawn on claim). **Done: the service query, `generateRenderer` and `validateSQL` serve, the aggregation extensions reject by name (ADR-0061); attachments moved from "no store" to a store face (ADR-0065) and a served surface (ADR-0066), implemented by the memory, SQL Server (ADR-0073) and PostGIS (ADR-0065 §2) providers.**
- T-D Stale-row guard: the §1 capability tables are checked against the mounted routes and the parameters the query parser reads, so a **Missing** row that names something served fails the architecture suite. **Done (SpatialEngine-itu, `tests/architecture/.../CompatibilityMatrixTests.cs`).**
