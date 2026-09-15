using System.Runtime.InteropServices;
using SkiaSharp;
using Spatial.Contracts;
using Spatial.Core.Geometry;
using Spatial.Rendering.Skia.Styling;

namespace Spatial.Rendering.Skia.Drawing;

/// <summary>One draw layer plus the geometries that survived filtering and shaping.</summary>
internal sealed record SceneLayer(DrawLayer Layer, IReadOnlyList<IGeometry> Geometries)
{
    /// <summary>The resolved symbol candidates (empty for non-symbol layers).</summary>
    public IReadOnlyList<SymbolFeature> Symbols { get; init; } = [];
}

/// <summary>One symbol candidate: the placed geometry plus the text/icon resolved from its attributes.</summary>
internal sealed record SymbolFeature(IGeometry Geometry, string? Text, string? Icon);

/// <summary>The ordered draw list and canvas background for one rasterization.</summary>
internal sealed record RenderScene(IReadOnlyList<SceneLayer> Layers, StyleColor? Background);

/// <summary>
/// The only class that draws with Skia. It accepts a compiled scene and core
/// geometry, and returns a premultiplied RGBA <see cref="RasterBuffer"/>;
/// no Skia type crosses this boundary (ADR-0005 applied to rendering).
/// </summary>
internal static class SkiaVectorRasterizer
{
    public static RasterBuffer Render(RenderScene scene, RasterViewport viewport, SpriteRegistry? sprites = null)
    {
        var info = new SKImageInfo(viewport.Width, viewport.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info)
            ?? throw new InvalidOperationException("Skia could not create a raster surface.");
        var projection = new ViewportProjection(viewport);
        surface.Canvas.Clear(scene.Background is { } background ? SkiaPaintFactory.ToSkia(background) : SKColors.Transparent);

        var hasSymbols = scene.Layers.Any(layer => layer.Layer.Paint is SymbolPaint);
        using var font = hasSymbols ? new BundledFont() : null;
        var collision = new List<SKRect>();
        foreach (var layer in scene.Layers)
        {
            Draw(surface.Canvas, layer, projection, collision, font, sprites ?? SpriteRegistry.Default);
        }

        using var image = surface.Snapshot();
        return ReadPixels(image, viewport);
    }

    private static void Draw(
        SKCanvas canvas, SceneLayer layer, ViewportProjection projection, List<SKRect> collision, BundledFont? font, SpriteRegistry sprites)
    {
        switch (layer.Layer.Paint)
        {
            case BackgroundPaint:
                break;
            case FillPaint fill:
                DrawFill(canvas, layer.Geometries, fill, projection);
                break;
            case LinePaint line:
                DrawLine(canvas, layer.Geometries, line, projection);
                break;
            case CirclePaint circle:
                DrawCircles(canvas, layer.Geometries, circle, projection);
                break;
            case SymbolPaint symbol:
                SkiaSymbolRasterizer.Draw(
                    new SymbolDrawContext(canvas, symbol.Options, projection, collision, font!, sprites), layer.Symbols);
                break;
        }
    }

    private static void DrawFill(SKCanvas canvas, IReadOnlyList<IGeometry> geometries, FillPaint fill, ViewportProjection projection)
    {
        using var path = SkiaPathBuilder.BuildFill(geometries, projection);
        if (path.IsEmpty)
        {
            return;
        }

        if (fill.Fill.Alpha > 0)
        {
            using var paint = SkiaPaintFactory.Fill(fill.Fill);
            canvas.DrawPath(path, paint);
        }

        if (fill.OutlineWidth > 0 && fill.Outline.Alpha > 0)
        {
            using var outline = SkiaPaintFactory.Stroke(fill.Outline, fill.OutlineWidth, LineCapStyle.Round, LineJoinStyle.Round);
            canvas.DrawPath(path, outline);
        }
    }

    private static void DrawLine(SKCanvas canvas, IReadOnlyList<IGeometry> geometries, LinePaint line, ViewportProjection projection)
    {
        using var path = SkiaPathBuilder.BuildLine(geometries, projection);
        if (path.IsEmpty)
        {
            return;
        }

        using var paint = SkiaPaintFactory.Stroke(line.Stroke, line.Width, line.Cap, line.Join);
        if (line.Dash.Count > 0)
        {
            paint.PathEffect = SKPathEffect.CreateDash([.. line.Dash.Select(value => (float)value)], 0);
        }

        canvas.DrawPath(path, paint);
    }

    private static void DrawCircles(SKCanvas canvas, IReadOnlyList<IGeometry> geometries, CirclePaint circle, ViewportProjection projection)
    {
        using var fill = SkiaPaintFactory.Fill(circle.Fill);
        using var stroke = circle.StrokeWidth > 0 && circle.Stroke.Alpha > 0
            ? SkiaPaintFactory.Stroke(circle.Stroke, circle.StrokeWidth)
            : null;

        foreach (var (x, y) in SkiaPathBuilder.Points(geometries, projection))
        {
            canvas.DrawCircle(x, y, (float)circle.Radius, fill);
            if (stroke is not null)
            {
                canvas.DrawCircle(x, y, (float)circle.Radius, stroke);
            }
        }
    }

    private static RasterBuffer ReadPixels(SKImage image, RasterViewport viewport)
    {
        var info = new SKImageInfo(viewport.Width, viewport.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        var bytes = new byte[info.BytesSize];
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            if (!image.ReadPixels(info, handle.AddrOfPinnedObject(), info.RowBytes, 0, 0))
            {
                throw new InvalidOperationException("Skia could not read the raster surface back.");
            }
        }
        finally
        {
            handle.Free();
        }

        return new RasterBuffer(bytes, viewport.Width, viewport.Height, info.RowBytes, RasterPixelFormat.Rgba8888);
    }
}
