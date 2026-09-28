# Map Service compatibility matrix

Sources (checked 2026-09-14):
- S1 Map Service: https://developers.arcgis.com/rest/services-reference/enterprise/map-service/
- S2 Export Map: https://developers.arcgis.com/rest/services-reference/enterprise/export-map/
- S3 Find: https://developers.arcgis.com/rest/services-reference/enterprise/find/
- S4 Child-op link graph on S1 (~40 `.../enterprise/<op>/` pages: `legend-map-service/`,
  `identify-map-service/`, `query-map-service-layer/`, `query-related-records-…`,
  `generate-kml/`, `generate-renderer-map-service-layer/`,
  `query-domains-map-service/`, `query-legends-map-service/`,
  `export-tiles-map-service/`, `estimate-export-tile-size-map-service/`,
  `map-tile/`, `tile-map/`, `wmts-*-map-service/`, `kml-image-map-service/`,
  `image-map-service/`, `html-popup-…`, `attachment-…`, `dynamic-layer-table/`,
  `ms-query-analytic/`, `map-service-job/result/input` (async)…)
- G1 Ground truth: `ground-truth/map-root.Census.json` (dynamic, `singleFusedMapCache:false`),
  `ground-truth/map-root-cached.WorldTopo.json` (`singleFusedMapCache` + `tileInfo` 24 LODs),
  `ground-truth/map-layers.Census.json`, `ground-truth/map-legend.Census.json`

Our surface: `GeoServicesEndpoints.Maps.cs` (routes), `MapServerOperationEndpoints.cs`
(root/layers/layer/query/identify/find/image), `MapServerLegendEndpoints.cs`
(legend/queryDomains/queryLegends), `MapOfflineRejects.cs`
(exportTiles/WMTS/KML/jobs), `MapVectorTileEndpoints.cs` (MVT),
`MapExportEndpoints` (`GeoServicesEndpoints.MapExport.cs`, export + `tile/{z}/{y}/{x}`),
`MapServerResources.cs`, `MapRenderEngine.cs`, `MapStyleProjection.cs`,
`MapGenerateRenderer.cs`, `MapLegend.cs`, `MapIdentifyPlan.cs`/`MapIdentifyMatcher.cs`.

## 1. Resources & operations

| ArcGIS capability | Ours | Status | Evidence |
|---|---|---|---|
| Service root (`mapName`, `layers[]`, `tables[]`, extents, `supportedImageFormatTypes`, `maxImageWidth/Height`, `supportsDynamicLayers`) | served | **Have** | `MapServerResources.cs:Root`; `GeoServicesEndpoints.Maps.cs` |
| `layers` (all layers+tables) | served | **Have** | `MapAllLayers`, replay vs G1 `map-layers` |
| `<layerId>` metadata + `drawingInfo`/`labelingInfo`/domains | served | **Have** | `MapServerResources.cs:Layer`; ADR-0050 projection |
| `<layerId>/query` (FeatureServer engine) | served | **Have** | `MapQuery` → `FeatureService.QueryAsync` |
| `identify` (with `layerDefs` filtering) | served | **Have** | `MapIdentifyEngine.cs`; `layerDefs` honoured (T-015 update) |
| `find` | served | **Have** | `MapFindEngine.cs` |
| `export` (png/jpg/webp/tiff; `bbox/bboxSR/imageSR/size/layers/layerOption/layerDefs/format/transparent/dpi`; `f=image` bytes or `{href}`) | served | **Have** | `GeoServicesEndpoints.MapExport.cs`; `MapRenderEngine.cs` (T-040: `time`/`layerTimeOptions`/`timeRelation`, `dynamicLayers`, `layerOption`, ADR-0058) |
| `tile/{z}/{y}/{x}` Web-Mercator tile | served | **Have** | `MapExportEndpoints`; `IMapRenderer`+`ITileScheme` (ADR-0046/0048) |
| `<layerId>/images/<imageId>` (§4.7 picture-symbol images) | typed `not.found` | **Non-goal** | `GeoServicesEndpoints.Maps.cs:MapImage` (ADR-0050: no picture symbols) |
| `legend` (per-layer symbology legend) | served | **Have** | `GeoServicesEndpoints.Maps.cs:29` → `MapServerLegendEndpoints.MapLegend` → `MapLegend.Legend`: one legend layer per published layer, projected from the persisted style so it always agrees with the layer metadata `drawingInfo`; replayed against G1 `map-legend.Census.json` (ADR-0055) |
| `dynamicLayers` / `dynamicLayerTable` (per-request layer redefinition) | served | **Have** | S1/S4; `export` rebinds `mapLayer` sources with a `drawingInfo` override over the projected subset (T-040, ADR-0058); new data sources stay honestly rejected |

| `generateRenderer` (server classification) | served | **Have** | `GeoServicesEndpoints.Maps.cs:35` → `MapServerOperationEndpoints.MapGenerateRenderer` → `MapGenerateRenderer.GenerateAsync`: equal-interval `classBreaksDef` and single-field `uniqueValueDef` over the layer's data, with the same typed rejects. The FeatureServer reuses this one classifier (ADR-0055, ADR-0061 §2) |
| `queryDomains` / `queryLegends` (service-level domain/legend queries) | served | **Have** | `GeoServicesEndpoints.Maps.cs:31,33` → `MapServerLegendEndpoints`: the projected domains and the per-layer legend of the selected layers (all layers when `layers` is absent), over the shared bare-id / show-hide-all selection grammar; an unknown layer id is a typed `not.found` (ADR-0055) |
| `queryRelatedRecords` (map-service variant) | — | **Missing** | no `MapServer/{layerId}/queryRelatedRecords` route is mounted: `MapServer/{layerId}/query` delegates to `FeatureService.QueryAsync` and relationship traversal is served on the FeatureServer surface over map-declared relationships (ADR-0077). Nothing on the map surface advertises it, so a client is not misled — it asks the FeatureServer |
| `exportTiles` + `estimateExportTileSize` (offline tile packages) | mounted, honestly rejected | **Non-goal** | `MapOfflineRejects.cs`: both resolve the service (an unknown one stays `not.found`) and then fail typed `invalid.arguments` — packaging needs a job model the host does not have (ADR-0033) — naming the live alternatives `tile/{z}/{y}/{x}` and `export` (ADR-0060 §1) |
| WMTS (`.../WMTS`, `WMTSClientCapabilities`, tile row) | mounted, honestly rejected | **Non-goal** | `.../MapServer/WMTS` and `.../MapServer/WMTS/{*rest}` (covering `WMTS/1.0.0/WMTSCapabilities.xml`) reject by name, naming `tile/{z}/{y}/{x}`; WMTS serving is closed, and live tiles come from the neutral and Esri routes (ADR-0060 §2) |
| KML (`generateKml`, `kml-image`) | mounted, honestly rejected | **Non-goal** | `generateKml` and `kml/{*rest}` (covering `kml/mapImage.kmz`) reject by name; no KML surface is served anywhere in the tree (ADR-0060 §3) |
| `htmlPopup` / attachments on map layers | — | **Missing** | no MapServer popup or attachment route is mounted, and the layer metadata is honest about it: `esriServerHTMLPopupTypeNone` and `hasAttachments: false` (`MapServerResources.Layer`). The served attachment surface is the FeatureServer, which advertises the face per store (ADR-0066); the map image resource is the separate typed-`not.found` row above |
| Live vector tiles (`MapServer/vectorTile/{z}/{y}/{x}`, `VectorTileServer/tile/{z}/{y}/{x}`) | served | **Have** | `MapVectorTileEndpoints.cs`: a live MVT view over the same service/scheme/cache contracts as the raster tiles, plus the map-scoped OGC landing, collections, TileJSON and negotiated tile data (ADR-0070, which superseded ADR-0062's "no MVT" decision). Offline `.vtpk` packaging stays a non-goal (ADR-0033, ADR-0060) |
| Async (`.../MapServer/jobs`, `map-service-job/result/input`) | mounted, honestly rejected | **Non-goal** | `.../MapServer/jobs` and `.../MapServer/jobs/{*rest}` (one job, its results, its inputs) reject by name: long-running work runs as cancellable tasks, not observable jobs, so there is nothing to poll and clients await the synchronous `export`/tile response (ADR-0033, ADR-0060 §4) |
| `time` / `layerTimeOptions` on export and identify | served | **Have** | `export` serves the temporal selection (T-040, ADR-0058) and `identify` now plans the same one, reusing the export grammar, so dated hits filter exactly as `query` and `export` do (`MapIdentifyPlan.cs`, `MapIdentifyMatcher`); live roots advertise `supportsTimeRelation`. `timeRelation` is validated on both paths but only the overlaps relation is applied — the one honest gap, stated in §2 |
| `layerOption` (`all\|visible\|top`), `gdbVersion`, `mapRangeValues`, `datumTransformations` on export | partial | **Partial** | `layerOption` validated and served (T-040, ADR-0058); `gdbVersion`/`mapRangeValues`/`datumTransformations` still ignored (speculative per §2) |

| Cached-service fields (`singleFusedMapCache`, `tileInfo` LODs, `storageInfo`, `exportTilesAllowed`) on root | served | **Have** | `singleFusedMapCache`+`tileInfo` per served scheme (LOD rows replayed vs G1, T-040), `exportTilesAllowed:false` (packaging is T-041); `storageInfo` honestly absent (no stored cache) |

## 2. Export-param detail (S2 vs `MapExport.cs`)

Have: `bbox`, `size`, `bboxSR`, `imageSR`, `layers`, `layerOption`,
`layerDefs`, `dynamicLayers`, `time`, `layerTimeOptions`, `timeRelation`,
`format`, `transparent`, `dpi`, `f` (T-040, ADR-0058). Missing:

`gdbVersion`, `mapRangeValues`, `geometries` (highlight), `rotation`,
`scale`/`mapScale` enforcement, `historicMoment` — speculative until a
client trace shows them.

**One honest gap in the temporal surface, stated rather than implied:**
`timeRelation` is parsed and validated on both `export` and `identify`
(`MapExportTime.ParseTimeRelation`) — an undocumented value is a typed
rejection — but the served window is the overlaps relation on both paths:
the parsed relation is not applied to the resolved time window
(`MapExportLayers.cs:38`, `MapIdentifyPlan.cs:31` discard it), while the
root advertises `supportsTimeRelation: true` (ADR-0058). The time window
that `time` and `layerTimeOptions` actually produce is correct for the
overlaps relation; `esriTimeRelationContains`/`Within` are accepted and
behave as overlaps. Applying the relation, or advertising the flag
truthfully, is filed as a follow-up rather than papered over here.

## 3. Follow-ups (filed)

- T-D Map missing resources: `legend`, `queryDomains`/`queryLegends`, `generateRenderer`
  (replay G1 `map-legend.Census.json` red-first). **Done (ADR-0055).**
- T-E Map export parity: `time`/`layerTimeOptions`, `dynamicLayers`, `layerOption`,
  cached-root fields honesty (`exportTilesAllowed`, `singleFusedMapCache`).
  **Done (ADR-0058).** The one row that did not close is the `timeRelation`
  application noted in §2, filed as its own follow-up.
- T-F Map offline/async surface: `exportTiles`+estimate, WMTS, KML, async jobs —
  scoping task (WMTS/KML detail spawns from `tiles.md` T-M). **Closed as
  documented non-goals, mounted and rejected by name (ADR-0060); vector tiles
  landed as a live surface (ADR-0070), which is the same close `tiles.md` T-M
  records.**
