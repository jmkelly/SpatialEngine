# Spatial.Rendering.Skia

The SkiaSharp vector renderer: labels, symbols, lines, polygons and the
exported image. Two records bind it: **ADR-0049** (labels and symbols shape
through bundled HarfBuzz over one embedded pinned font, sprites are embedded
SVG, and `Skia.HarfBuzz` / `Svg.Skia` are allowlisted packages) and
**ADR-0044** (raster rendering is a pipeline over Skia for vector and NetVips
for imagery). Route by task: `architecture/distilled/rendering.md`.

## Never

- No second font source and no system font lookup: the embedded pinned font
  is the font, and the pinned HarfBuzz version is what shapes it (ADR-0049).
  A new `Skia.*` package is an allowlist entry, which needs an ADR.
- No raster value or Skia type on a contract; the renderer returns encoded
  bytes and core-typed metadata (ADR-0051).
- No placement decided by iteration order: collision resolution is
  deterministic, and the candidate and priority rules are ADR-0080's.
- No style value parsed here that the style layer already parsed — one
  interpretation, in one place (ADR-0050).
- No GDAL and no second raster engine in the pipeline (ADR-0044, ADR-0051).

## Commands

- `dotnet test tests/unit/Spatial.Rendering.Skia.Tests` — the renderer over
  checked-in fixtures, including the label-placement battery.
- `dotnet test tests/unit/Spatial.Imagery.Vips.Tests` — the imagery half.
- `eng/workbench-e2e.sh` — the exported image in a real client.
