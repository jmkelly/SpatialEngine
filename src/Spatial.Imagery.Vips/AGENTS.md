# Spatial.Imagery.Vips

The NetVips raster provider: decode, resample, mosaic and encode of imagery
rasters. Two records bind it: **ADR-0051** (rasters are provider-owned, so a
contract carries an encoded image plus core-typed metadata and nothing that
lives in this process) and **ADR-0044** (raster rendering is a pipeline over
NetVips for imagery and Skia for vector). Route by task:
`architecture/principles.md`.

## Never

- No `NetVips` type, and no live image object, on a contract or across a
  service boundary — an encoded image plus core-typed metadata is what
  crosses (ADR-0051).
- No GDAL "because it is easier": NetVips is the engine and GDAL waits for
  measured demand (ADR-0051).
- No mutating a raster the caller owns; every stage returns a new image and
  the pipeline's order is the record's (ADR-0044).
- No unbounded decode: an image larger than the requested extent is
  windowed, not loaded, and the failure mode is a typed refusal.
- No cache key that omits the encoding, the resampling or the band layout —
  an image cache is content-addressed (ADR-0046).

## Commands

- `dotnet test tests/unit/Spatial.Imagery.Vips.Tests` — the provider over
  checked-in rasters; no container needed.
- `dotnet test tests/unit/Spatial.Rendering.Skia.Tests` — the other half of
  the pipeline (ADR-0044).
- `eng/verify.sh --fast` — the lane a change here is handed off on.
