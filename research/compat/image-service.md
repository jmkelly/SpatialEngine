# Image Service compatibility matrix

Sources (checked 2026-09-14):
- S1 Image Service: https://developers.arcgis.com/rest/services-reference/enterprise/image-service/
- S2 Export Image: https://developers.arcgis.com/rest/services-reference/enterprise/export-image/
- S3 Child-op link graph on S1 (~60 pages: `export-image/`, `identify-image-service/`,
  `query-image-service/`, `download-rasters/`, `raster-catalog-item/`, `raster-info/`,
  `raster-image/`, `raster-thumbnail/`, `raster-file/`, `raster-attribute-table/`,
  `raster-histograms/`+`compute-histograms/`+`compute-statistics-and-histograms/`,
  `legend-image-service/`, `find-image-service/`, `measure…/`, `multidimensional-info/`+
  `slices/`, `colormap/`, `mosaic-rules/`, `raster-function-infos/objects/`,
  `export-tiles-image-service/`, `tile-map/`, `wmts-*-image-service/`,
  `kml-image-image-service/`, `image-service-job/result/input`, mensuration pages…)
- G1 Ground truth: `ground-truth/image-root.CharlotteLAS.json` (LAS catalog service,
  full key list incl. `bandCount/pixelType/serviceDataType/fields/allowCopy/…`)

Our surface: `GeoServicesEndpoints.Images.cs`, `ImageService.cs`,
`RasterCatalogQuery.cs`, SDK `IRasterCatalogue` (ADR-0051).

## 1. Resources & operations

| ArcGIS capability | Ours | Status | Evidence |
|---|---|---|---|
| Service root (extent, pixel size, band count/type, `serviceDataType`, catalog `fields`/`objectIdField`) | served | **Have** | `ImageService.cs:Root`; replay G1 keys |
| `exportImage` (png/jpg/tiff; `bbox/bboxSR/imageSR/size`, interpolation, compression, pixelType, noData; `f=image` bytes or `{href}` JSON) | served | **Have** | `GeoServicesEndpoints.Images.cs:293-317`; `ImageService.cs:Parse*` |
| `identify` (pixel values + catalog items) | served | **Have** | `Images.cs`; `IRasterCatalogue.IdentifyAsync` |
| Catalog `query` (safe `where` subset, `objectIds`, geometry, `outFields`, order, paging, ids/count/extent/distinct, `outSR`) | served | **Have** | `RasterCatalogQuery.cs` |
| `{rasterId}` item + `{rasterId}/info` | served | **Have** | `Images.cs:194,214` |
| `{rasterId}/image` (§8.2 one item's image), `{rasterId}/thumbnail` (§8.3) | served | **Have** | `Images.cs` |
| `download` (§8.0.7 ids) + `file` (§8.5 bytes, opt-in, size/file-capped, range-capable, opaque provider ids) | served | **Have** | `Images.cs:388-462`; `Spatial:GeoServices:AllowRasterDownload` |
| COG/pyramidal reads (block size + pyramid levels reported, export from chosen overview) | served | **Have** | `IRasterCatalogue`/`VipsRasterCatalogue` (ADR-0051) |
| `mosaicRule` / `renderingRule` / `bandIds` | honestly rejected | **Non-goal** | rejected by name on export + catalog query (`EsriFeatureQuery.cs:489-491`, §7.1); ignoring them would serve wrong pixels |
| Download clipping/re-encoding, raster functions (`raster-function-infos/objects`, `colormap` apply) | — | **Non-goal** | ADR-0051 I5/I6; `allowCopy/allowAnalysis` honesty only |
| `legend` | — | **Missing** | S3 `legend-image-service/`; no route |
| `find` (image-service search) | — | **Missing** | S3 `find-image-service/` |
| `raster-attribute-table`, `histograms`/`statistics` resources, `multidimensional-info`/`slices`, `key-properties`, `metadata`/`raster-metadata`, `thumbnail` (service-level) | — | **Missing** | S3; our root reports stored band stats (`RasterInfo`) but serves no stats/histogram/RAT resources |
| Mensuration (`measure/`, `measure-from-image/`, `compute-angles/`, `project-…`, `image-to-map…`, `query-gps`, `query-boundary`) | — | **Non-goal** | S3; needs sensor models we don't have; reject-by-name not yet wired (goes to T-H scope) |
| `exportTiles` + estimate (image), WMTS, KML | — | **Missing** | S3; same offline surface as Map (see `tiles.md`) |
| Async (`image-service-job/result/input`), `get-image-url`, `add-rasters/delete-rasters/update-raster/uploads` (editing) | — | **Non-goal** | S3; no job model; catalog is read-only (ingest is the neutral write path, ADR-0041) |
| Root-field deltas vs G1: `advancedQueryCapabilities`, `allowRasterFunction`, `allowedMosaicMethods/defaultMosaicMethod/mosaicOperator`, `rasterFunctionInfos`, `rasterTypeInfos`, `mensurationCapabilities`, `hasColormap/hasHistograms/hasRasterAttributeTable`, `maxDownloadImageCount/SizeLimit`, `serviceSourceType`, `sortField/Value` | — | **Partial** | G1 lists all; we emit the render-relevant subset. Clients branching on `allowRasterFunction`/`rasterFunctionInfos` need honest flags (T-H) |

## 2. Follow-ups (filed)

- T-G Image missing resources: `legend`, `find`, raster attribute table +
  statistics/histograms resources, service-level `thumbnail`/`metadata`
  (red-first against G1 + crafted fixtures).
- T-H Image capability honesty: `allowRasterFunction`/`rasterFunctionInfos`/
  `allowedMosaicMethods`/`mensurationCapabilities` flags + explicit
  reject-by-name for mensuration/multidimensional/write ops.
