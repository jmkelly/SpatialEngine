---
status: accepted
date: 2026-09-14
deciders: maintainer + agent
---

# ADR-0059: ImageServer capability honesty — raster-function/mosaic/mensuration flags + named rejects

## Context

The Image Service compatibility review (`research/compat/image-service.md`
§1, row T-H, task T-043) compared the served root against the ground truth
`research/compat/ground-truth/image-root.CharlotteLAS.json` (G1). The root
reported the render-relevant subset (extent, pixel size, band count/type,
`serviceDataType`, catalog `fields`/`objectIdField`) but omitted every flag
a client branches on: `allowRasterFunction`/`rasterFunctionInfos`,
`allowedMosaicMethods`/`defaultMosaicMethod`/`mosaicOperator`,
`mensurationCapabilities`, `hasColormap`/`hasHistograms`/
`hasRasterAttributeTable`, `maxDownloadImageCount`/`maxDownloadSizeLimit`
(G1 `maxDownloadSizeLimit`), and `serviceSourceType`. A client reading the
G1 shape would assume raster functions, mosaicking and mensuration work;
they do not. Meanwhile the mensuration, multidimensional and catalog-write
operations had no routes at all and fell through to a generic framework 404
with no Esri envelope.

T-042 (ADR-0054) landed first and owns the missing resources; it
deliberately left every root flag untouched, so this record builds on its
behaviours (`statistics`, `computeHistograms`, `rasterAttributeTable` and
their 404s) without reworking them. `mosaicRule`/`renderingRule`/`bandIds`,
download clipping/re-encoding and raster functions stay non-goals: the
export and catalog-query paths already reject them by name (T-015), and
honouring them is out of scope.

## Decision

Serve truthful flags on the ImageServer root, each proved by the behaviour
it names (the T-019 pattern):

1. **`allowRasterFunction: false`, `rasterFunctionInfos: []`** — the
   engine serves no raster functions. Proved by the `renderingRule` reject
   on export, catalog query, legend, attribute table and
   `computeHistograms` (T-015/T-042 tests).
2. **`allowedMosaicMethods: "None"`, `defaultMosaicMethod: "None"`,
   `mosaicOperator: "First"`** — on-the-fly mosaicking is unsupported:
   `mosaicRule` is rejected and multi-raster `rasterIds` are rejected, so
   at most one raster's native pixels are ever served. `"None"` is in the
   Esri mosaic-method vocabulary (G1 lists it); `"First"` describes the
   single addressed raster winning by default. Proved by the `mosaicRule`
   and multi-`rasterIds` rejects.
3. **`mensurationCapabilities: "None"`** — the catalog carries no sensor
   models. Proved by the named mensuration rejects below.
4. **`hasColormap: false`** — no colormap is applied: `renderingRule` is
   rejected and legend swatches are raw export renders (ADR-0054).
5. **`hasHistograms`** — true exactly when `computeHistograms` can serve
   the dataset, i.e. its pixel type is 8-bit (`RasterPixelType.U8`), the
   only format the v1 provider path computes (ADR-0054). A float raster
   reports `false` and its `computeHistograms` answers 400.
6. **`hasRasterAttributeTable`** — true exactly when the dataset carries a
   configured table (`RasterInfo.AttributeTable`), mirroring the resource
   that serves it or answers a typed `not.found` (ADR-0054).
7. **`maxDownloadImageCount` / `maxDownloadSizeLimit`** — the enforced
   host caps (`GeoServicesOptions.MaxRasterDownloadFiles` /
   `MaxRasterDownloadBytes`), proved by the download file-count and byte
   rejects.
8. **`serviceSourceType: "esriImageServiceSourceTypeDataset"`** — the
   engine serves one provider-owned dataset, never a mosaic dataset: the
   root describes a single dataset and export addresses single rasters.

Wire explicit GET+POST reject-by-name routes (400 `invalid.arguments` with
an Esri envelope naming the operation) for:

- **Mensuration** — `measure`, `measureFromImage`, `computeAngles`,
  `project`, `projectImage`, `imageToMap`, `mapToImage`, `queryGPSInfo`,
  `queryBoundary` (research S3 `measure/`, `measure-from-image/`,
  `compute-angles/`, `project-…`, `image-to-map…`, `query-gps`,
  `query-boundary`): sensor models we do not have.
- **Multidimensional** — `multidimensionalInfo`, `slices` (research S3
  `multidimensional-info/`, `slices/`): multidimensional rasters are not
  served.
- **Catalog writes** — `addRasters`, `deleteRasters`, `updateRaster`,
  `uploads`, `upload`: the catalog is read-only; ingest is the neutral
  write path (ADR-0041).

No SDK or Core change: the flags shape existing SDK types
(`RasterInfo.PixelType`/`AttributeTable`) and host options, and the
rejects are synchronous adapter routes (no provider call, so no
cancellation path to test — same precedent as the T-015 rejects).

## Consequences

- Clients branching on `allowRasterFunction`, mosaic methods or
  `mensurationCapabilities` now read honest values instead of assuming
  the G1 shape works.
- The sixteen rejected operations answer 400 with the operation name and
  reason instead of a generic framework 404.
- Still un-emitted G1 keys (`advancedQueryCapabilities`,
  `rasterTypeInfos`, `sortField`/`sortValue`) stay partial; follow-ups
  need measured client demand.
- Follow-ups deliberately left out: serving any raster function,
  mosaicking method, mensuration op, multidimensional slice, or catalog
  write (each needs models or a write path the engine does not have).

## Alternatives

- **Omitting the flags** (status quo): rejected — G1-shaped clients
  assume the capabilities exist.
- **`allowedMosaicMethods: ""`**: rejected — `"None"` is the Esri
  vocabulary value for no mosaicking and G1 itself lists it.
- **Honest-404 stubs per operation**: rejected — a 400 naming the
  operation and reason tells the client what is wrong; a 404 suggests a
  wrong URL.
