---
status: accepted
date: 2026-09-14
deciders: maintainer + agent
---

# ADR-0054: ImageServer missing resources — legend, find, statistics/histograms, attribute table, thumbnail/metadata

## Context

The Image Service compatibility review (`research/compat/image-service.md`
§1, row T-G, task T-042) found seven Esri resources with no engine route:
`legend` (S3 `legend-image-service/`), `find` (S3 `find-image-service/`),
the raster attribute table (S3 `raster-attribute-table/`), statistics and
histograms (S3 `statistics/`, `raster-histograms/`, `compute-histograms/`),
and the service-level `thumbnail` and `metadata`. The root already reported
stored band statistics and each catalog item had `info`/`image`/`thumbnail`,
but nothing served the stored stats, computed histograms, class tables, or
dataset-wide legend/thumbnail/metadata.

The exact wire shapes were pinned live against the reference before
building: `CharlotteLAS/ImageServer/legend`, `/statistics`,
`/computeHistograms`, `/1/info` (which carries `statistics` and a null
`histograms`), and `NLCDLandCover2001/ImageServer/rasterAttributeTable` and
`/legend` for the classified shapes, plus the S3 `compute-histograms`,
`raster-attribute-table`, `legend-image-service`, `find-image-service` and
`metadata` pages.

Three findings constrained the design:

1. S3 `find-image-service/` is **not** a text search: it is the 11.2
   oriented-imagery inspection workflow (`fromGeometry`/`toGeometry` camera
   positions). The engine's catalog items carry no sensor models, so that
   workflow cannot be served honestly.
2. S3 `metadata` always returns the authored XML document. The engine keeps
   no authored (ISO/FGDC) metadata store, so byte-fidelity is impossible.
3. The root capability flags (`hasHistograms`, `hasRasterAttributeTable`,
   `allowRasterFunction`, …) belong to T-043 (capability honesty) and are
   deliberately untouched here.

## Decision

Serve seven dataset-level resources on the ImageServer, all GET+POST like
the existing surface, with failures as structured codes
(`invalid.arguments`, `not.found`, `store.unavailable`):

1. **`legend?f=json`** — one entry per band labelled `Band_N` (the Esri
   `bandNames` convention), each with the 20×20 dataset render as base64
   `imageData`. The swatch is produced by the existing export pipeline, so
   it shows what `exportImage` serves without inventing a renderer the
   engine does not have. `bandIds` (0-based) selects entries and is
   validated; `renderingRule`/`variable` are rejected by name (T-015
   precedent: honouring them is out of scope, ignoring them serves wrong
   symbology). `legendType` is `Stretched`, or `RGB Composite` for 3/4-band
   selections.
2. **`find`** — a catalog text search over string fields mirroring the
   MapServer `find` shape (`searchText`, `contains`, `searchFields`,
   `returnGeometry`, `sr`; results carry `layerId: 0`, `foundFieldName`,
   `value`, `attributes`, `geometry`). It requires a catalog (400
   otherwise) and rejects `fromGeometry`/`toGeometry` by name: the
   oriented-imagery workflow needs sensor models we do not have.
3. **`statistics?f=json`** — the dataset's stored band statistics as
   `{min, max, mean, standardDeviation, skipX: 1, skipY: 1, count: 0}`,
   replaying the reference shape for stored stats. No stored stats is a
   typed `not.found`, never an invented table.
4. **`computeHistograms`** — `IRasterCatalogue.ComputeHistogramsAsync`
   over the requested envelope/polygon (new SDK method, ADR-0051 style:
   core types only). The provider projects the bounds, clips to the raster
   extent and returns one 256-bin full-range histogram per band, matching
   the S3 response (`{size, min: -0.5, max: 255.5, counts}`). Only 8-bit
   bands are supported in v1; other formats are `invalid.arguments`.
   `mosaicRule`/`renderingRule`/`pixelSize`/`time`/
   `processAsMultidimensional` are rejected by name. Histograms are
   computed from the dataset raster: mosaicking catalog items stays a
   non-goal, so no per-item dimension is invented.
5. **`rasterAttributeTable?f=json`** — the configured value-frequency
   table in the NLCD shape (`objectIdFieldName`, `fields` with the first
   column as `esriFieldTypeOID`, `features` with attributes only).
   Contract: SDK `RasterAttributeTable` (core `AttributeValue` rows) on
   `RasterInfo`, populated from `RasterDatasetDescriptor.AttributeTable`;
   misconfigured tables fail fast with `invalid.arguments` at catalogue
   construction. No configured table is a typed `not.found` (the reference
   serves the resource only when the raster has a table).
6. **`thumbnail`** — the whole-dataset reduced image (spec §8.3 semantics),
   streamed by default and as `{href, width, height}` for `f=json`,
   mirroring the per-item thumbnail.
7. **`metadata?f=json`** — the described dataset as JSON (name,
   description, extent, band count/type, stats, copyright). This diverges
   from the reference XML deliberately: see finding 2 above.

`{rasterId}/info` additionally reports stored per-band statistics
(`[[min, max, mean, stddev]]`, as the reference does) when the item stores
them, omitted otherwise.

## Consequences

- Root output and every capability flag are unchanged; T-043 owns flag
   honesty (`hasHistograms`, `hasRasterAttributeTable`, …).
- New SDK surface is core-typed only (`RasterHistogram`,
   `RasterHistogramRequest`, `RasterAttributeTable`,
   `RasterAttributeField`); NetVips stays inside `Spatial.Imagery.Vips`
   and no package is added (`Directory.Packages.props` untouched).
- Cancellation flows end to end (`Task` + token checks per band; HTTP
   maps to 499); provider misconfiguration fails at startup, request
   errors map to 400/404.
- Follow-ups deliberately left out: classified legends driven by RAT
   colours, non-8-bit histograms (`hist_find_ndim` needs measured demand,
   ADR-0021), per-item `{rasterId}/metadata`, authored XML metadata, and
   the oriented-imagery `find` (sensor models).

## Alternatives

- **Honest-404 stubs for RAT/statistics** (MapServer §4.7 precedent):
   rejected — configured tables and stored stats are real data with a
   verified reference shape, and the plumbing is small.
- **Per-item `{rasterId}/histograms` resource**: no such Esri resource
   exists (probed: falls back to the item); the `computeHistograms`
   operation is the reference path.
- **Generating legend swatches in the adapter**: rejected — renderer
   types and pixel handling stay inside the owning implementation
   (hard wall); the export pipeline already returns encoded bytes.
