# Image Service (ImageServer) Implementation Plan

> **Status:** I0–I2 implemented (ADR-0051). The raster boundary is fixed
> (provider-owned rasters; encoded images + core-typed metadata cross a
> contract), the NetVips path is chosen over GDAL pending measured demand,
> and the GeoServices ImageServer serves root metadata, raster info, catalog
> listing/item, identify and `exportImage` over a `PublicationKind.Image`
> publication. I3 (catalog `query`/`download`/file resources) and I4 (cache
> and limits) remain. Read
> `architecture/references/geoservices-compatibility.md` §4 and
> `architecture/distilled/host-and-clients.md` first.
>
> **Blocking decision (resolved):** the engine had no raster concept
> (ADR-0035 listed image as out of scope). ADR-0051 fixes the boundary and the
> engine path: the existing NetVips `IRasterOperations`/imagery implementation
> is extended with a core-typed `IRasterCatalogue` face in
> `Spatial.Imagery.Vips`; only encoded image bytes, core metadata and core
> geometry footprints cross contracts. GDAL is a measured-demand follow-up.

## 1. What the spec requires (v1.0 §8)

| Resource / operation | Depends on |
| --- | --- |
| Image Service root (§8.0): `extent`, `pixelSizeX/Y`, `bandCount`, `pixelType`, `min/max/mean/stdv` values, `serviceDataType`, `fields`, `objectIdField` | raster metadata |
| Export Image (§8.0.4) | **raster reader + warp/render + image encoding** |
| Query (Image Services) (§8.0.5) | raster catalog (a table of raster items) |
| Identify (§8.0.6) | pixel/band sampling at a point |
| Download Rasters (§8.0.7) | raw raster byte streaming |
| Raster Catalog Item (§8.1) | catalog row |
| Raster Image / Thumbnail / Info / File (§8.2–8.5) | raster data + metadata |
| Raster functions, key properties, legend (modern 10.x additions) | raster processing pipeline — **non-goal** |

The catalog-less case matters: Query and Download are defined only when the
service has an accessible catalog.

## 2. Context

- The engine is headless and its contracts carry canonical geometry/feature
  values only. There is no `Raster` type, no band model, no nodata/mask
  concept, no raster read path, and no image encoding.
- `PublicationKind.Image` (ADR-0041) is the natural
  carrier: an ImageServer is a named raster source (single raster or
  catalog) plus metadata, exposed by a publication.
- The MapServer data-only pattern applies here too: a
  metadata-only ImageServer root is cheap and useful for discovery before
  any pixels are produced.

## 3. Decision: the raster boundary

| Option | Shape | Cost / risk |
| --- | --- | --- |
| **A. Provider-owned rasters (recommended)** | rasters never become core values; an implementation project owns COG/GDAL/tiling; the wire carries **encoded image bytes + JSON metadata**; footprints/extents cross as core geometry | keeps core small (principles 1, 17); needs a raster engine dependency |
| **B. Core raster value type** | `Raster`/`Band`/tile types in `Spatial.Core`, raster verbs in the SDK | large core change; pulls a whole raster model into the value kernel; contradicts ADR-0001/0032 |
| **C. Facade over external services only** | ImageServer is a proxy over remote imagery | no local data; SSRF/token/config; not a real image implementation |
| **D. Client-side only** | leave raster to the MapLibre client; no ImageServer | no Esri imagery interop at all |

**Decided direction:** **A**. Rasters are provider-owned and never cross a
contract as raster values — only encoded images and metadata do. This is
the same shape ADR-0035 used for Esri JSON and is the only option that
keeps the core a geometry/feature value model. The engine choice (managed
COG reader vs GDAL) is settled **inside** the I0 ADR, with measured demand
as ADR-0021 requires.

**Engine choice (part of the same ADR):** a managed COG/GeoTIFF reader plus
a warp/sample implementation, or GDAL (native packages, container
implications, JIT-only). GDAL is the pragmatic breadth choice (mosaics,
warps, many formats) but is a native dependency and needs a package
allowlist entry + ADR; a managed path is narrower (COG only) but keeps the
"framework-only" project rule. Decide with measured demand, as ADR-0021
demands for AOT.

## 4. Phases

### I0 — Raster boundary ADR — **delivered**
- **Deliverable:** ADR-0051 fixes option A (provider-owned rasters; encoded
  images + core-typed metadata/geometry only), chooses the managed NetVips
  path over GDAL (ADR-0021 measured-demand rule), models the catalog as a
  core `FeatureSchema` of raster items and bounds pixel-type conversion.
- **Delivered:** `Spatial.PluginSdk.IRasterCatalogue` and its core-typed
  records/enums (`RasterInfo`, `RasterBandStatistics`,
  `RasterDatasetDescription`, `RasterCatalogItem`, `RasterIdentifyRequest`,
  `RasterIdentifyResult`, `RasterExportRequest`, `RasterPixelType`,
  `RasterInterpolation`); architecture tests pin the SDK's package-free,
  core-typed surface.
- **Proof:** `Spatial.Architecture.Tests` (SDK references Core + framework
  only; the public surface names no third-party type).

### I1 — Raster catalogue and metadata — **delivered**
- **Deliverable:** `PublicationKind.Image` publications; a raster-catalogue
  provider; Image Server root metadata, raster info, catalog item/listing
  and identify; footprints/extents as core geometry.
- **Delivered:** `Spatial.Imagery.Vips.VipsRasterCatalogue` (describe,
  list, identify, export) over configured dataset descriptors;
  `Spatial.Host` registers the keyed `raster` catalogue from
  `Spatial:Raster`; the GeoServices adapter serves `/{service}/ImageServer`,
  `/{service}/ImageServer/{rasterId}` and `.../{rasterId}/info`,
  `.../query` (catalog listing) and `.../identify`.
- **Proof:** provider unit tests (metadata, catalog, footprint round-trip,
  identify); host HTTP tests (spec metadata fixture, catalog fields/list,
  absent-catalog rejection).

### I2 — Export Image — **delivered**
- **Deliverable:** `exportImage` with `bbox` + `size` (or `bboxSR`/
  `imageSR`), `format` (png/jpg/tiff), `interpolation`, `compression`,
  `pixelType`, `noData`; `f=image` streaming and the JSON `href` shape;
  unsupported pixel types rejected explicitly.
- **Delivered:** `IRasterCatalogue.ExportAsync` (crop, resample, cast,
  nodata alpha, encode) and the adapter's `exportImage` route with bbox
  reprojection through `ICoordinateTransforms`.
- **Proof:** golden export at a fixed extent/size, nodata/transparency,
  CRS transform correctness and pixel-type rejection (provider unit tests +
  host HTTP tests).

### I3 — Catalog operations
- **Deliverable:** `query` over the image catalog (reuse the safe `where`
  subset), `download` raw rasters, raster thumbnail/image/file resources.
- **Proof:** catalog query fixtures; download size/format caps; range
  handling if the provider supports it.

### I4 — Scale, cache and limits
- **Deliverable:** tile/export caching policy, request size caps,
  concurrency limits, cancellation over HTTP (client disconnect cancels
  raster work); optional pre-tiled/COG mosaicking.
- **Proof:** cancellation and cap tests; load-shape check that export does
  not park a request.

### I5 — (Explicit non-goal) raster analytics
Raster functions, statistics computation beyond stored metadata, on-the-fly
mosaicking, and multidimensional/time-aware imagery are recorded as
non-goals unless a separate ADR adopts them.

## 5. Non-goals (standing)

Raster analytics and raster-function chains; spectral indices; server-side
time-series; elevation/point-cloud services; editing imagery. The engine
serves *existing* imagery; it does not become a raster processing engine.

## 6. Risks

- Native dependency (GDAL) breaks the framework-only packages rule and the
  Docker image size/attestation story; a managed alternative may be too
  narrow for mosaics.
- Pixel correctness (nodata, masks, palette, bit depth, resampling) is
  easy to get subtly wrong; ownership of band statistics is unclear
  (stored vs computed).
- The raster catalog is a feature-like dataset with a raster per row;
  decide whether the catalog is `PublicationKind.Image` metadata or a
  normal dataset plus a raster locator column, before I3.
- `download` exposes raw data — authorization and size limits are
  mandatory, not optional.

## 7. References

- `architecture/distilled/contracts.md` (Publication/registry/admin)
- `architecture/distilled/host-and-clients.md` (M0 data-only pattern; shared render path)
- `architecture/references/geoservices-compatibility.md` §4
- ADR-0001/0032 (core values), ADR-0021 (native/AOT evidence), ADR-0035, principles 1–2
