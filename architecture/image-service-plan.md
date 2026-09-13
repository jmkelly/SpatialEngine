# Image Service (ImageServer) Implementation Plan

> **Status:** I0–I3 implemented (ADR-0051). The raster boundary is fixed
> (provider-owned rasters; encoded images + core-typed metadata cross a
> contract), the NetVips path is chosen over GDAL pending measured demand,
> and the GeoServices ImageServer serves root metadata, raster info, catalog
> listing/item/query, identify, `exportImage`, and the catalog file surface
> (`download`, Raster Image/Thumbnail/File) over a `PublicationKind.Image`
> publication. **I4 — COG and tiled GeoTIFF support** is implemented; I5
> (cache and limits beyond the download caps) remains. Read
> `architecture/references/geoservices-compatibility.md` §4 and
> `architecture/distilled/host-and-clients.md` first.
>
> **Blocking decision (resolved):** the engine had no raster concept
> (ADR-0035 listed image as out of scope). ADR-0051 fixes the boundary and the
> engine path: the existing NetVips `IRasterOperations`/imagery implementation
> is extended with a core-typed `IRasterCatalogue` face in
> `Spatial.Imagery.Vips`; only encoded image bytes, core metadata and core
> geometry footprints cross contracts. GDAL is a measured-demand follow-up;
> tiled and internally-overviewed (pyramidal) TIFFs — including COG — are
> already inside libvips and are scheduled as I4 without it.

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
as ADR-0021 requires. The managed reader is NetVips' `tiffload`, which
already handles the tiled/pyramidal structure a COG adds; phase I4 turns
that into block/pyramid metadata, overview-aware export and COG writing.

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

### I3 — Catalog operations — **delivered**
- **Deliverable:** `query` over the image catalog (reuse the safe `where`
  subset), `download` raw rasters, raster thumbnail/image/file resources.
- **Delivered:** the ImageServer catalog `query` reuses the Feature query
  engine (safe `where`, `objectIds`, geometry, `outFields`,
  `orderByFields`, paging, ids/count/extent/distinct and `outSR`);
  `IRasterCatalogue` gains `ListFilesAsync`/`ReadFileAsync` over opaque
  provider-owned file ids; the adapter serves `download` (with per-request
  size/file caps, opt-in via `Spatial:GeoServices:AllowRasterDownload`),
  `file` (range-capable streaming), `{rasterId}/image` and
  `{rasterId}/thumbnail`. Clipping a download and re-encoding it are typed
  rejections, not silent passes.
- **Proof:** provider unit tests (file listing/reading, id validation,
  per-item export); host HTTP tests (query filter/order/count, bad `where`
  and `time` rejection, image/thumbnail bytes, download caps, ranged file
  streaming); `RasterOptions` catalog-config projection tests.

### I4 — COG and tiled GeoTIFF support — **delivered**

Raster imagery is normally stored as a **tiled GeoTIFF**, and a **Cloud
Optimized GeoTIFF (COG)** is that plus an internal overview pyramid stored
before the data. The managed NetVips path already reads and writes both — no
GDAL, no new package (ADR-0051 option A is unchanged: structure stays
provider-owned and only core metadata crosses). Before this phase the
provider read a tiled/pyramidal file as if it were a stripped image and
hardcoded the block and pyramid fields of `RasterInfo` to 0, so the extra
structure was paid for and then thrown away.

- **Deliverable:**
  1. **Read the structure.** `VipsRasterReader.ReadInfo` reads
     `tile-width`/`tile-height` and `n-subifds` from the file and fills
     `RasterInfo.BlockWidth`/`BlockHeight`/`FirstPyramidLevel`/
     `MaxPyramidLevel` (absent fields stay 0 for striped rasters);
     `VipsRasterFiles.Open` opens with random access so a tiled raster is
     read by tile, not streamed.
  2. **Use the overviews.** `RasterPyramid.Select` picks the coarsest
     overview whose resolution still covers the requested output, and
     `VipsRasterExporter` opens it (`Image.Tiffload(path, subifd: level - 1)`)
     and maps the window onto it before cropping and resampling, so an
     `exportImage` or tile from a large COG reads a fraction of the pixels.
     It never upscales: if an overview turns out coarser than assumed, the
     exporter falls back to full resolution. Selection and mapping are pure
     helpers, unit-tested independently.
  3. **Write COG-style output.** The concrete
     `VipsRasterCatalogue.WriteCogAsync` rewrites a configured raster with
     `Tiffsave(tile: true, tileWidth: 256, tileHeight: 256, pyramid: true,
     subifd: true, compression: Deflate, predictor: Horizontal, bigtiff:
     false)` so an uploaded stripped GeoTIFF can be published as a COG. It is
     deliberately not on the `IRasterCatalogue` contract and becomes an
     ingest/seed verb when a raster ingest path lands.
  4. **Honest metadata.** The ImageServer root derives
     `minPixelSize`/`maxPixelSize` from the pyramid factor and raster info
     reports the real block/pyramid numbers instead of 0.
- **Research findings** (verified on this machine against the pinned NetVips
  3.2.0 / libvips 8.18.6):
  - `Image.Tiffload(filename, subifd: i, access: Enums.Access.Random)`
    selects an internal overview and random access;
    `Image.Tiffsave(..., tile, tileWidth, tileHeight, pyramid, subifd,
    compression, predictor, bigtiff)` writes the COG-style file.
  - A generated 4096² tiled+pyramidal TIFF reports `n-subifds=4`,
    `tile-width=256`, `tile-height=256`; `subifd` 0–3 are
    2048²/1024²/512²/256², a 256² crop took ~80 ms (ranged, not a full
    decode), and `Image.Thumbnail` selected an overview automatically.
  - A striped TIFF exposes neither field, so the new metadata is a no-op
    for the current fixtures; `NetVips.Image.Get` throws on a missing
    field, so presence is checked via `GetFields()`.
  - A local path is opened directly. Remote/HTTP COG range reads need a
    custom `VipsSource` (and an HTTP client libvips does not bundle) and
    stay a separate decision; georeferencing is still descriptor-supplied
    because libvips does not expose the GeoTIFF GeoKey tags.
- **Non-goals for this phase:** HTTP COG range reading, on-the-fly
  mosaicking, GeoKey parsing, and a GDAL backend (the ADR-0051
  measured-demand triggers are unchanged). Writing a COG is a repackaging
  step to serve existing imagery, not raster analytics.
- **Delivered:** `VipsRasterStructure` reads `tile-width`/`tile-height`/
  `n-subifds`; `VipsRasterReader.ReadInfo` fills the `RasterInfo` block and
  pyramid fields; `VipsRasterFiles.Open` uses random access;
  `RasterPyramid.Select`/`ScaleWindow` choose and map the overview;
  `VipsRasterExporter` reads the chosen overview before cropping and
  resampling; `VipsRasterCog` writes a tiled pyramidal file and
  `VipsRasterCatalogue.WriteCogAsync` exposes it; `ImageService` derives the
  service `minPixelSize`/`maxPixelSize` from the pyramid depth and reports
  the real block/pyramid raster-info values.
- **Proof:** provider tests (structure on tiled vs striped fixtures, level
  selection and window mapping, full-resolution and downscaled exports,
  offset overview window, COG round-trip); adapter tests (pyramid raster
  info and pixel-size bounds); the host root reports the pyramid pixel-size
  bounds end to end.

### I5 — Scale, cache and limits
- **Deliverable:** tile/export caching policy, request size caps,
  concurrency limits, cancellation over HTTP (client disconnect cancels
  raster work); optional pre-tiled/COG mosaicking (now buildable on I4).
- **Proof:** cancellation and cap tests; load-shape check that export does
  not park a request.

### I6 — (Explicit non-goal) raster analytics
Raster functions, statistics computation beyond stored metadata, on-the-fly
mosaicking, and multidimensional/time-aware imagery are recorded as
non-goals unless a separate ADR adopts them.

## 5. Non-goals (standing)

Raster analytics and raster-function chains; spectral indices; server-side
time-series; elevation/point-cloud services; editing imagery. The engine
serves *existing* imagery; it does not become a raster processing engine.
Repackaging a stored raster as a tiled, internally-overviewed COG so it can
be served efficiently (I4) is a storage concern, not analytics.

## 6. Risks

- Native dependency (GDAL) breaks the framework-only packages rule and the
  Docker image size/attestation story; a managed alternative may be too
  narrow for mosaics.
- Pixel correctness (nodata, masks, palette, bit depth, resampling) is
  easy to get subtly wrong; ownership of band statistics is unclear
  (stored vs computed).
- The raster catalog is a feature-like dataset with a raster per row;
  decide whether the catalog is `PublicationKind.Image` metadata or a
  normal dataset plus a raster locator column, before I3. **Resolved:** the
  catalog stays `PublicationKind.Image` provider metadata with a core
  `FeatureSchema` (ADR-0051 §3); `query` reuses the feature query engine
  over it rather than promoting it to a store dataset.
- `download` exposes raw data — authorization and size limits are
  mandatory, not optional. **Resolved:** raw download is opt-in
  (`Spatial:GeoServices:AllowRasterDownload`), bounded by per-request
  size/file caps, and serves only provider-vetted opaque file ids — never a
  caller-supplied path.

## 7. References

- `architecture/distilled/contracts.md` (Publication/registry/admin)
- `architecture/distilled/host-and-clients.md` (M0 data-only pattern; shared render path)
- `architecture/references/geoservices-compatibility.md` §4
- ADR-0001/0032 (core values), ADR-0021 (native/AOT evidence), ADR-0035, principles 1–2
