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

Our surface: `GeoServicesEndpoints.Maps.cs` (root/layers/layer/query/identify/find/image),
`MapExportEndpoints` (`GeoServicesEndpoints.MapExport.cs`, export + `tile/{z}/{y}/{x}`),
`MapServerResources.cs`, `MapRenderEngine.cs`, `MapStyleProjection.cs`.

## 1. Resources & operations

| ArcGIS capability | Ours | Status | Evidence |
|---|---|---|---|
| Service root (`mapName`, `layers[]`, `tables[]`, extents, `supportedImageFormatTypes`, `maxImageWidth/Height`, `supportsDynamicLayers`) | served | **Have** | `MapServerResources.cs:Root`; `GeoServicesEndpoints.Maps.cs` |
| `layers` (all layers+tables) | served | **Have** | `MapAllLayers`, replay vs G1 `map-layers` |
| `<layerId>` metadata + `drawingInfo`/`labelingInfo`/domains | served | **Have** | `MapServerResources.cs:Layer`; ADR-0050 projection |
| `<layerId>/query` (FeatureServer engine) | served | **Have** | `MapQuery` → `FeatureService.QueryAsync` |
| `identify` (with `layerDefs` filtering) | served | **Have** | `MapIdentifyEngine.cs`; `layerDefs` honoured (T-015 update) |
| `find` | served | **Have** | `MapFindEngine.cs` |
| `export` (png/jpg/webp/tiff; `bbox/bboxSR/imageSR/size/layers/layerOption/layerDefs/format/transparent/dpi`; `f=image` bytes or `{href}`) | served | **Have** | `GeoServicesEndpoints.MapExport.cs`; `MapRenderEngine.cs` (T-040: `time`/`layerTimeOptions`/`timeRelation`, `dynamicLayers`, `layerOption`, ADR-0056) |
| `tile/{z}/{y}/{x}` Web-Mercator tile | served | **Have** | `MapExportEndpoints`; `IMapRenderer`+`ITileScheme` (ADR-0046/0048) |
| `<layerId>/images/<imageId>` (§4.7 picture-symbol images) | typed `not.found` | **Non-goal** | `GeoServicesEndpoints.Maps.cs:MapImage` (ADR-0050: no picture symbols) |
| `legend` (per-layer symbology legend) | — | **Missing** | S4 `legend-map-service/`; G1 `map-legend.Census.json` is the replay fixture; no route in `Maps.cs` |
| `dynamicLayers` / `dynamicLayerTable` (per-request layer redefinition) | served | **Have** | S1/S4; `export` rebinds `mapLayer` sources with a `drawingInfo` override over the projected subset (T-040, ADR-0056); new data sources stay honestly rejected |
| `generateRenderer` (server classification) | — | **Missing** | S4; same gap as Feature (shared classification engine absent) |
| `queryDomains` / `queryLegends` (service-level domain/legend queries) | — | **Missing** | S4 `query-domains-map-service/`, `query-legends-map-service/` |
| `queryRelatedRecords` (map-service variant) | — | **Missing** | S4; no relationship model |
| `exportTiles` + `estimateExportTileSize` (offline tile packages) | — | **Missing** | S4; our tiles are live-rendered only, no packaging/job model (ADR-0033: no jobs) |
| WMTS (`.../WMTS`, `WMTSClientCapabilities`, tile row) | — | **Missing** | S4 `wmts-*-map-service/`; we serve no WMTS endpoint (see `tiles.md`) |
| KML (`generateKml`, `kml-image`) | — | **Missing** | S4; no KML surface anywhere in tree |
| `image` (map image resource) / `htmlPopup` / attachments on map layers | — | **Missing** | S4; same non-model gaps as Feature |
| Async (`.../MapServer/jobs`, `map-service-job/result/input`) | — | **Non-goal** | S4; no job model by architecture (ADR-0033) |
| `time` / `layerTimeOptions` / `timeRelation` on export/identify | partial | **Partial** | live roots advertise `supportsTimeRelation` and `export` honours all three (T-040, ADR-0056); `identify` still ignores `time` (follow-up) |
| `layerOption` (`all\|visible\|top`), `gdbVersion`, `mapRangeValues`, `datumTransformations` on export | partial | **Partial** | `layerOption` validated and served (T-040, ADR-0056); `gdbVersion`/`mapRangeValues`/`datumTransformations` still ignored (speculative per §2) |
| Cached-service fields (`singleFusedMapCache`, `tileInfo` LODs, `storageInfo`, `exportTilesAllowed`) on root | served | **Have** | `singleFusedMapCache`+`tileInfo` per served scheme (LOD rows replayed vs G1, T-040), `exportTilesAllowed:false` (packaging is T-041); `storageInfo` honestly absent (no stored cache) |

## 2. Export-param detail (S2 vs `MapExport.cs`)

Have: `bbox`, `size`, `bboxSR`, `imageSR`, `layers`, `layerOption`,
`layerDefs`, `dynamicLayers`, `time`, `layerTimeOptions`, `timeRelation`,
`format`, `transparent`, `dpi`, `f` (T-040, ADR-0056). Missing:
`gdbVersion`, `mapRangeValues`, `geometries` (highlight), `rotation`,
`scale`/`mapScale` enforcement, `historicMoment` — speculative until a
client trace shows them.

## 3. Follow-ups (filed)

- T-D Map missing resources: `legend`, `queryDomains`/`queryLegends`, `generateRenderer`
  (replay G1 `map-legend.Census.json` red-first).
- T-E Map export parity: `time`/`layerTimeOptions`, `dynamicLayers`, `layerOption`,
  cached-root fields honesty (`exportTilesAllowed`, `singleFusedMapCache`).
- T-F Map offline/async surface: `exportTiles`+estimate, WMTS, KML, async jobs —
  scoping task (WMTS/KML detail spawns from `tiles.md` T-K).
