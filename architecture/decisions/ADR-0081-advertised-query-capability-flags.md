---
status: accepted
date: 2026-09-28
deciders: maintainer + agent
summary: The layer metadata advertises the two remaining capability flags the served query surface earns (`supportsQuantization` and the advanced-query pair) and nothing else.
---

# ADR-0081: Advertise the capability flags the served query surface earns

## Context

ADR-0079 made `quantizationParameters`, `maxAllowableOffset` and
`geometryPrecision` real generalization on the Feature Service `query`
path. It explicitly left one consequence unclaimed:

> `supportsQuantization` is still not advertised on the layer metadata; the
> layer response advertises no such capability flags at all today, so the
> ArcGIS REST JS `supportsQuantization` gate stays false.

The gate is not cosmetic. `@esri/arcgis-rest-*` (and the JS API behind the
tiled web-map client) reads `supportsQuantization` off the layer resource and
only sends `quantizationParameters` when it is true
(`research/arcgis/conformance-sources.md` §1.1/§1.2, T8). So the engine
served a real capability, described it honestly in an ADR, and the client
never asked for it.

The T3/T2 research pass had already flagged the whole flag family
(`research/arcgis/conformance-sources.md` §1.2 lists
`supportsPagination`, `supportsStatistics`, `supportsOrderBy`,
`supportsDistinct`, `supportsHavingClause`, `supportsCountDistinct`,
`supportsPercentileStatistics`, `supportsReturningQueryExtent`,
`supportsPaginationOnAggregatedQueries`, `supportsQueryWithDistance`,
`supportsTrueCurve`, `supportsLod`, `supportsQuantization`,
`supportsFullTextSearch`, `useStandardizedQueries`). ADR-0057 had already
claimed five of them. The rest of the family was still unsaid, and the rule
established by ADR-0057 is the rule this record applies: **a flag is
advertised exactly when behaviour proves it, and a flag the facade does not
earn is omitted rather than emitted as `false`.**

## Decision

**The layer metadata advertises the two remaining flags the served query
surface earns, and nothing else new. Every other flag in the family stays
absent.**

### 1. `supportsQuantization` (layer root and `advancedQueryCapabilities`)

True, on both the top-level layer key the REST JS gate reads and the
`advancedQueryCapabilities` object the S3 layer reference pins it under
(§1.2 lists it in the same object as the rest of the flags). It is proved by
`GeoServicesSpatialRelTests.Quantization_returns_a_quantized_geometry` and the
ADR-0079 deviation tests: the query path snaps x/y/z/m onto the requested
view grid anchored on the extent.

The top-level key is **per layer**, and a table — a dataset served under
`tables` because its schema carries no geometry field — omits it. A table has
no coordinates to quantize, so advertising a coordinate grid for it would be
the kind of lie this decision exists to prevent.

The `advancedQueryCapabilities` object is the description of the *query
surface*, which is the same code for every layer, so it stays the one shared
static the service root also advertises. A service whose layers are all
tables therefore carries `advancedQueryCapabilities.supportsQuantization`
while its table layers omit the per-layer key; the shared object describes
the endpoint, the per-layer key describes the data.

### 2. `supportsPaginationOnAggregatedQueries`

True. `FeatureStatisticsEngine` applies the same `resultOffset` /
`resultRecordCount` page and reports `exceededTransferLimit` plus
`resultPaginationToken` for an `outStatistics` response
(`GeoServicesStatisticsTests.Statistics_pages_report_exceeded_limit`).
The keyset token path (`RelatedQuery` rejects
`resultPaginationToken` on the related-records route) does not change that:
aggregated results are paged by offset over the same materialised group rows.

### 3. Everything else stays absent

- `supportsQuantizationEditMode` — quantization is query-side only; the edit
  path (`applyEdits`/`addFeatures`/`updateFeatures`) does not read the
  parameter, so the flag would be false in substance and is omitted.
- `supportsTrueCurve`, `supportsLod`, `supportsQueryWithDistance`,
  `supportsQueryWithDatumTransformation`, `supportsQueryAnalytic`,
  `supportsSqlExpression`, `supportsQueryWithResultType`,
  `supportsQueryAttachments` — no engine path; each corresponding request
  parameter is rejected by name, which is the honest alternative to a flag.
- `supportsCalculate`, `supportsValidateSQL`, `supportsRollbackOnFailureParameter`
  and the `advancedQueryAnalyticCapabilities` /
  `advancedEditingCapabilities` objects — write-model operations the facade
  does not serve.
- `supportsFullTextSearch` stays `false` with an empty
  `fullTextSearchableFields` list: `text` is rejected by name, and an
  explicit `false` is what tells a client to send a LIKE filter instead of
  the parameter.

## Consequences

- The tiled REST JS client now sends `quantizationParameters` and gets the
  quantized response it asked for, against a layer that has been serving it
  since ADR-0079. A client that gated on the flag changes behaviour; one that
  did not is unaffected.
- The layer response gains keys. Any test asserting an exact key set on the
  layer resource would need updating; the repo's tests assert named
  properties, and the two new names are now asserted positively and
  negatively (`GeoServicesFormatTests`).
- `supportsQuantization: true` is a claim the engine must keep honouring. If
  quantization is ever removed or rejected by name again, this flag has to go
  with it — that coupling is the point.
- Nothing crosses a contract boundary. `EsriLayer` and
  `EsriAdvancedQueryCapabilities` are Esri wire shapes inside
  `Spatial.Adapter.GeoServices` (ADR-0005, principle 8); `Spatial.Contracts`,
  the .NET/TypeScript SDKs and the workbench are untouched, because they read
  features, not this metadata.

## References

- ADR-0005 (implementation types stay in their implementation)
- ADR-0035 (GeoServices boundary adapter)
- ADR-0057 (feature statistics capability honesty — the pattern)
- ADR-0079 (deviation allowance geometry verb — the served capability)
- `architecture/references/geoservices-compatibility.md`
- `research/arcgis/conformance-sources.md` §1.2, T8
- `research/compat/feature-service.md` §1–2
