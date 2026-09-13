---
status: accepted
date: 2026-09-16
deciders: maintainer + agent
---

# ADR-0051: Rasters are provider-owned; only encoded images and core-typed metadata cross contracts

## Context

`architecture/image-service-plan.md` scopes a GeoServices ImageServer (spec
§8) on the engine. Its **blocking decision** (I0) is the raster boundary: the
engine has no raster concept anywhere. `Spatial.Core` holds geometry and
feature values only (ADR-0001/0032); `Spatial.PluginSdk` is interfaces and
DTOs over those values, references only `Spatial.Core` and takes no packages
(ADR-0033); ADR-0035 listed the Image Service as explicitly out of scope
("no raster pipeline").

The plan offers four options and recommends **A — provider-owned rasters**:
rasters never become core values; an implementation project owns the raster
engine; the wire carries encoded image bytes plus JSON metadata;
footprints/extents cross as core geometry. Option B (a core `Raster`/`Band`
model) contradicts ADR-0001/0032, option C (a proxy over remote imagery)
is not a real image implementation and carries SSRF/config risk, and option
D (client-only) has no Esri imagery interop at all.

Three further questions must be settled in the same record:

1. **Engine path.** `Spatial.Imagery.Vips` already implements
   `IRasterOperations` over NetVips (read/normalise/compose/encode) for
   ADR-0044. NetVips can read GeoTIFF/COG, crop, resize, cast band formats,
   and encode PNG/JPEG/TIFF. GDAL would add mosaics and many formats but is a
   native dependency with container/attestation cost. ADR-0021 requires
   measured demand, not preference, before a heavier engine.
2. **Catalog model.** An ImageServer root advertises `fields` and
   `objectIdField` only when the service has an accessible raster catalog
   (spec §8.0.2). The catalog is a feature-like table of raster items with
   footprints; a single-raster service has none.
3. **Pixel-type conversion.** Export accepts a requested `pixelType`; some
   conversions are nonsensical (for example a three-band colour raster to a
   complex type) and must be rejected explicitly, not silently coerced.

Standing constraints apply unchanged: ADR-0005 (no third-party type crosses a
contract), ADR-0033 (in-process contracts, implementations composed by DI),
ADR-0041 (`PublicationKind.Image` publications reuse the registry and admin
surface), ADR-0044 (`IRasterOperations` is the image-encoding seam), and
ADR-0040 (new code clears the metrics/CRAP/coverage gates).

## Decision

**1. Option A is fixed: rasters are provider-owned.**

A raster never becomes a `Spatial.Core` value. There is no `Raster`, `Band`
or tile type in the core, and no raster algorithm in the SDK. Raster values
stay inside the owning implementation project; what crosses a contract is:

- an **encoded image** (`RasterImage`, already core-typed: `byte[]` plus a
  media type and dimensions), and
- **core-typed metadata** (`Envelope`, `CoordinateReference`-style CRS
  identity as a string, counts, pixel-type/resampling enums) plus **core
  geometry footprints** (`IGeometry`) and core `AttributeValue` attributes.

Paths, file handles, `Vips.Image`, `SKImage`, `Npgsql` and GeoTIFF tags never
cross. This is the same shape ADR-0035 used for Esri JSON and ADR-0020 uses
for features: the foreign representation lives at the implementation edge.
Identify is the one verb that reports numbers sampled from a raster; those
are a small scalar per-band sample (a value tuple for the identified point),
not a raster/band/tile model, and are the minimum the spec §8.0.6 response
needs. No raster object, band array or tile ever crosses.

**2. The engine path is the existing NetVips `IRasterOperations`, extended by
an `IRasterCatalogue` face in `Spatial.Imagery.Vips`.**

The managed NetVips path is chosen, not GDAL, because it already exists in
the repository, already owns the encode seam (ADR-0044), and already satisfies
the "framework-only packages" rule under an approved allowlist. The
ImageServer requirements (read a GeoTIFF/COG, crop/resize, resample, cast
band formats, apply nodata transparency, encode PNG/JPEG/TIFF) are inside
NetVips' capability. Tiled and internally-overviewed (pyramidal) TIFFs, the
structure a Cloud Optimized GeoTIFF adds, are read with `tiffload(
subifd: n)`/random access and written with `tiffsave(tile, pyramid,
subifd)`, so COG support does not need GDAL either (scheduled as plan I4).
GDAL is **not** adopted now: it would add a native
package and a Docker/attestation surface without a measured need. Per
ADR-0021 the decision is demand-driven; adopting GDAL (mosaicking beyond the
configured catalog, GeoKey parsing, hundreds of formats) requires a new ADR
with a measured workload first.

Concretely:

- A new core-typed `IRasterCatalogue` interface sits in `Spatial.PluginSdk`
  (root namespace, beside `IRasterOperations`). It describes a raster
  dataset, lists catalog items (when a catalog exists), identifies catalog
  items and samples the pixel at a point, and exports a warped/encoded image.
- `Spatial.Imagery.Vips` implements `IRasterCatalogue` as
  `VipsRasterCatalogue`. It reuses the assembly's existing NetVips helpers
  (`ImageryLoader`, `VipsEncoder`) and adds only the raster dataset
  descriptors, band-format mapping and export/identify verbs. No new project
  is created, so no new package allowlist entry is needed.
- Georeferencing (extent, CRS, pixel size) is declared by the **provider
  descriptor**, not parsed from GeoTIFF tags: libvips/NetVips is an imagery
  library and does not expose the GeoTIFF `ModelPixelScale`/`GeoKeyDirectory`
  tags. This is the honest cost of the managed path and the recorded trigger
  for the GDAL follow-up. The tiled/pyramidal *storage* metadata
  (`tile-width`/`tile-height`/`n-subifds`) is exposed and is used for block
  and pyramid reporting (plan I4); only the georeferencing keys are not.
  Raster width/height/band count/pixel type are read
  from the file (NetVips metadata); extent/CRS/pixel size come from the
  descriptor.

**3. A raster catalog is provider data, modelled on the feature face.**

The catalog is a table of raster items with a core `FeatureSchema`: an
integer identity (`OBJECTID`), a geometry footprint, and typed attributes.
`IRasterCatalogue.DescribeAsync` returns `RasterDatasetDescription` whose
`HasCatalog`/`ObjectIdField`/`CatalogSchema` are `null`/`false` for a
single-raster service. Query/item/identify-catalog operations reject a
catalog-less service with a typed failure; `identify` still samples the pixel
and returns no `catalogItems`. Footprints are core `IGeometry`; the adapter
maps them to Esri JSON through `Spatial.Interop.Esri`, exactly as it does for
features.

**4. Pixel-type conversion is explicit and bounded.**

`RasterExportRequest.PixelType` (and the equivalent parameter on raster item
export) is a core enum in the SDK. The provider casts band formats only for a
documented compatible set — the real (non-complex) integer and floating-point
formats `U8`/`U16`/`S16`/`U32`/`S32`/`F32`/`F64` — and rejects everything
else (`C64`, `C128`, `U1`, `U2`, `U4`, `Unknown`) with `invalid.arguments`.
A colour raster (three or more bands) may only stay at an 8-bit integer type,
so a colour → `F32`/`S16` request is rejected rather than flattened. The
adapter maps the SDK enum to the Esri `pixelType` strings; no Esri-named type
enters the SDK.

**5. The ImageServer is a projection of a `PublicationKind.Image` publication.**

No new registry or admin concept is introduced. An image publication names a
keyed raster store (the `raster` key) and one or more raster datasets; the
GeoServices adapter resolves the publication, then the keyed
`IRasterCatalogue`, and maps spec §8 onto the SDK verbs. `PublicationKind.Image`
already exists (ADR-0041); the catalog advertises it as `ImageServer`. The
non-goals stand: raster functions, spectral indices, on-the-fly mosaicking
beyond the configured catalog, multidimensional/time imagery, and download
clipping/re-encoding are out of scope; catalog `query` and raw `download` are
I3 and are served through the same provider-owned boundary.

## Consequences

- `Spatial.Core` and `Spatial.PluginSdk` stay package-free and core-typed.
  One new SDK interface (`IRasterCatalogue`) and its value records are added;
  architecture tests assert the SDK references only `Spatial.Core` and
  framework assemblies, and exposes no third-party type on its public
  surface.
- `Spatial.Imagery.Vips` gains a second contract implementation; it still
  references only `Spatial.Core` + `Spatial.PluginSdk`. No new project,
  package or allowlist entry is required.
- `Spatial.Host` registers the raster catalogue from `Spatial:Raster`
  configuration (sources and optional catalogs) under the keyed `raster`
  store. The GeoServices adapter consumes only the SDK contract.
- Georeferencing is descriptor-supplied. A future GDAL-backed provider (or a
  NetVips GeoKey reader, if one appears) can replace it behind the same
  contract; the trigger is a measured need for tag parsing, mosaics or
  additional formats, recorded here and in the plan.
- The ImageServer serves metadata, item listing/query, identify, raster info,
  `exportImage` (encoded bytes via `f=image`, JSON `href` otherwise), the
  Raster Image/Thumbnail resources, and the raw `download`/Raster File
  resources (opt-in, size-capped, opaque provider file ids). Raster
  functions/analytics remain an explicit non-goal.
- Raw download is a second consumer of the provider's files: `ListFilesAsync`
  / `ReadFileAsync` return an opaque `RasterFile.Id` and bytes, and the
  provider validates the id against the files it owns, so no path ever
  crosses the boundary or reaches a caller.
- Nodata and transparency: `noData` produces an alpha mask so PNG/JPEG
  exports treat those pixels as transparent (PNG) or background (JPEG);
  `format`, `interpolation` and `compressionQuality` map to NetVips
  encode/resize options, with unsupported values rejected as
  `invalid.arguments`.

## Alternatives

- **A core raster value model (option B)** — a `Raster`/`Band`/tile kernel.
  Rejected: it pulls a large foreign model into the value kernel and
  contradicts ADR-0001/0032/0005.
- **GDAL first** — the pragmatic breadth choice (mosaics, warps, many
  formats). Rejected for now: a native dependency without a measured need,
  against ADR-0021; it needs its own ADR and package allowlist entry when
  demand is demonstrated. Recorded as the follow-up.
- **A new `Spatial.Provider.Raster` project** — separates "raster provider"
  from "imagery pipeline". Rejected for I0–I2 because it would own the same
  NetVips dependency as `Spatial.Imagery.Vips`, duplicating the package and
  splitting one imagery seam across two projects. If a non-NetVips raster
  engine arrives (for example GDAL), it is a sibling implementation of
  `IRasterCatalogue` and earns its own project then.
- **Parsing GeoTIFF GeoKeys in the managed path** — would remove the
  descriptor for CRS/extent. Rejected because neither libvips nor NetVips
  exposes those tags reliably; a hand-rolled TIFF/GeoTIFF parser is a raster
  engine of its own and a poor use of the core. GDAL is the proper owner.
- **Proxy over an external image service (option C)** — not a local image
  implementation, plus SSRF/token/config concerns (ADR-0035 rule). Rejected.

## Implementation status

I0–I3 of `architecture/image-service-plan.md`:
`Spatial.PluginSdk.IRasterCatalogue` and its core-typed records/enums
(including `RasterFile`/`RasterFileContent` and the file verbs);
`Spatial.Imagery.Vips.VipsRasterCatalogue` (describe, list, identify, export,
list/read files); `Spatial.Host` registration from `Spatial:Raster` (single
rasters and configured catalogs); the `PublicationKind.Image` GeoServices
ImageServer projection (root, raster info, catalog item/listing/query,
identify, `exportImage`, Raster Image/Thumbnail and the opt-in Download
Rasters/Raster File surface). Not implemented: I4 COG/tiled-GeoTIFF
structure awareness (block/pyramid metadata, overview-aware export and
COG-style writing); I5 in-memory export caching beyond the download caps;
raster functions/statistics computation (I6 non-goal).

## References

- `architecture/image-service-plan.md` — I0–I4 delivery plan
- `architecture/references/geoservices-compatibility.md` §4 and the spec PDF §8
- ADR-0001/0032 (core values), ADR-0005 (no third-party types in contracts),
  ADR-0021 (native/AOT evidence), ADR-0035 (GeoServices boundary adapter),
  ADR-0041 (publications), ADR-0044 (raster rendering / `IRasterOperations`)
