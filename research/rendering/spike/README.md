# Rendering spike

A runnable vertical slice of [ADR-0042](../../../architecture/decisions/ADR-0042-raster-rendering-pipeline.md):
real `Spatial.Core` geometry → `ICoordinateTransforms` → `IGeometryOperations`
→ Skia vector raster → NetVips imagery composite/encode.

**This is research code.** It is deliberately *outside* `/src` and `/tests`
and *not* in `SpatialEngine.slnx`, so the architecture guards
(`Solution_membership_is_exact`, package allowlists) and `eng/verify.sh`
ignore it. It opts out of central package management and pins its own
package versions. Do not promote it; promote its findings into an ADR and a
real project.

## Run

```bash
cd research/rendering/spike/RenderSpike
dotnet run -c Release                                # -> /tmp/render-spike/sample.png
dotnet run -c Release -- --bench                     # per-stage + parallel numbers
dotnet run -c Release -- --imagery /path/base.tif    # composite over real imagery
```

Requires .NET 10. `SkiaSharp.NativeAssets.Linux.NoDependencies` and
`NetVips.Native.linux-x64` are pinned, so no system Skia/libvips is needed
(a system libvips works too; 8.18.6 was used for the recorded numbers).

## Files

| File | Stage it stands in for |
| --- | --- |
| `Program.cs` | composition root + benchmark driver |
| `Viewport.cs` | viewport DTO + Web Mercator projection |
| `StyleSheet.cs` | CSS-ish authoring → compiled per-layer paint recipes |
| `SkiaVectorRenderer.cs` | `IMapRenderer`'s raster stage (the only Skia surface) |
| `ImageryCompositor.cs` | `IRasterOperations` (the only NetVips surface) |
| `SpikeData.cs` | sample geometry + a deterministic benchmark network |

## What it proved

- Core geometry renders correctly with fill/line/circle styles and composes
  over NetVips imagery into a PNG.
- Parallel tiles scale ~13× on 24 cores (65 tiles/s end-to-end for a
  32k-vertex, 512² case).
- The integration traps recorded in the research report (sRGB
  interpretation before `composite2`, equal band counts, premultiplied
  alpha, `SKPathBuilder`).
