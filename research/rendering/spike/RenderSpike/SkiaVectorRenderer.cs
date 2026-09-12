namespace RenderSpike;

using SkiaSharp;
using Spatial.Core.Geometry;
using Spatial.PluginSdk;

/// <summary>A source layer instance: an id that the stylesheet keys on, plus geometry.</summary>
public sealed record RenderLayer(string Id, IGeometry Geometry);

/// <summary>
/// The vector rasterizer. This is the only place that knows Skia exists: it
/// accepts core geometry + a compiled <see cref="StyleSheet"/>, and returns a
/// premultiplied RGBA surface. Nothing Skia-flavoured crosses this boundary
/// (ADR-0005 applied to rendering).
/// </summary>
public static class SkiaVectorRenderer
{
    public static SKBitmap Render(
        IReadOnlyList<RenderLayer> layers,
        StyleSheet styles,
        Viewport viewport,
        IGeometryOperations? operations = null,
        double simplifyTolerance = 0)
    {
        var info = new SKImageInfo(viewport.Width, viewport.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info)
            ?? throw new InvalidOperationException("Skia could not create a raster surface.");
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        canvas.Save();
        canvas.ClipRect(SKRect.Create(viewport.Width, viewport.Height));

        foreach (var layer in layers)
        {
            if (!styles.TryGet(layer.Id, out var style))
            {
                continue;
            }

            var geometry = simplifyTolerance > 0 && operations is not null
                ? operations.Simplify(layer.Geometry, simplifyTolerance)
                : layer.Geometry;

            switch (style)
            {
                case FillStyle fill:
                    DrawFill(canvas, geometry, fill, viewport);
                    break;
                case LineStyle line:
                    DrawLine(canvas, geometry, line, viewport);
                    break;
                case CircleStyle circle:
                    DrawCircles(canvas, geometry, circle, viewport);
                    break;
            }
        }

        canvas.Restore();
        using var image = surface.Snapshot();
        return SKBitmap.FromImage(image);
    }

    private static void DrawFill(SKCanvas canvas, IGeometry geometry, FillStyle style, Viewport viewport)
    {
        using var builder = new SKPathBuilder { FillType = SKPathFillType.EvenOdd };
        AddPolygons(builder, geometry, viewport);
        using var path = builder.Detach();
        if (path.IsEmpty)
        {
            return;
        }

        if (style.Fill.Alpha > 0)
        {
            using var paint = Paint(SKPaintStyle.Fill, style.Fill, style.Opacity);
            canvas.DrawPath(path, paint);
        }

        if (style.OutlineWidth > 0 && style.Outline.Alpha > 0)
        {
            using var outline = Paint(SKPaintStyle.Stroke, style.Outline, style.Opacity);
            outline.StrokeWidth = style.OutlineWidth;
            outline.StrokeJoin = SKStrokeJoin.Round;
            canvas.DrawPath(path, outline);
        }
    }

    private static void DrawLine(SKCanvas canvas, IGeometry geometry, LineStyle style, Viewport viewport)
    {
        using var builder = new SKPathBuilder();
        AddLines(builder, geometry, viewport);
        using var path = builder.Detach();
        if (path.IsEmpty)
        {
            return;
        }

        using var paint = Paint(SKPaintStyle.Stroke, style.Stroke, style.Opacity);
        paint.StrokeWidth = style.Width;
        paint.StrokeCap = SKStrokeCap.Round;
        paint.StrokeJoin = SKStrokeJoin.Round;
        if (style.Dash.Length > 0)
        {
            paint.PathEffect = SKPathEffect.CreateDash(style.Dash, 0);
        }

        canvas.DrawPath(path, paint);
    }

    private static void DrawCircles(SKCanvas canvas, IGeometry geometry, CircleStyle style, Viewport viewport)
    {
        using var fill = Paint(SKPaintStyle.Fill, style.Fill, style.Opacity);
        using var stroke = style.StrokeWidth > 0 && style.Stroke.Alpha > 0
            ? Paint(SKPaintStyle.Stroke, style.Stroke, style.Opacity)
            : null;
        if (stroke is not null)
        {
            stroke.StrokeWidth = style.StrokeWidth;
        }

        foreach (var point in Points(geometry))
        {
            var (x, y) = viewport.ToPixel(point.X, point.Y);
            canvas.DrawCircle(x, y, style.Radius, fill);
            if (stroke is not null)
            {
                canvas.DrawCircle(x, y, style.Radius, stroke);
            }
        }
    }

    private static SKPaint Paint(SKPaintStyle paintStyle, SKColor color, float opacity) => new()
    {
        Style = paintStyle,
        Color = opacity >= 1f ? color : color.WithAlpha((byte)Math.Clamp(color.Alpha * opacity, 0, 255)),
        IsAntialias = true,
    };

    private static void AddPolygons(SKPathBuilder path, IGeometry geometry, Viewport viewport)
    {
        switch (geometry)
        {
            case IPolygon polygon:
                AddRing(path, polygon.ExteriorRing, viewport);
                foreach (var hole in polygon.InteriorRings)
                {
                    AddRing(path, hole, viewport);
                }

                break;
            case IMultiPolygon multi:
                foreach (var child in multi.Polygons)
                {
                    AddRing(path, child.ExteriorRing, viewport);
                    foreach (var hole in child.InteriorRings)
                    {
                        AddRing(path, hole, viewport);
                    }
                }

                break;
            case IGeometryParts parts:
                foreach (var child in parts.Geometries)
                {
                    AddPolygons(path, child, viewport);
                }

                break;
        }
    }

    private static void AddLines(SKPathBuilder path, IGeometry geometry, Viewport viewport)
    {
        switch (geometry)
        {
            case ILineString line:
                AddRing(path, line, viewport);
                break;
            case IMultiLineString multi:
                foreach (var child in multi.LineStrings)
                {
                    AddRing(path, child, viewport);
                }

                break;
            case IGeometryParts parts:
                foreach (var child in parts.Geometries)
                {
                    AddLines(path, child, viewport);
                }

                break;
        }
    }

    private static IEnumerable<Coordinate> Points(IGeometry geometry)
    {
        switch (geometry)
        {
            case IPoint point when point.Coordinate is { } coordinate:
                yield return coordinate;
                break;
            case IMultiPoint multi:
                foreach (var child in multi.Points)
                {
                    if (child.Coordinate is { } coordinate)
                    {
                        yield return coordinate;
                    }
                }

                break;
            case IGeometryParts parts:
                foreach (var child in parts.Geometries)
                {
                    foreach (var coordinate in Points(child))
                    {
                        yield return coordinate;
                    }
                }

                break;
        }
    }

    private static void AddRing(SKPathBuilder path, ILineString ring, Viewport viewport)
    {
        var sequence = ring.Sequence;
        var started = false;
        for (var i = 0; i < sequence.Count; i++)
        {
            var coordinate = sequence.GetCoordinate(i);
            if (!double.IsFinite(coordinate.X) || !double.IsFinite(coordinate.Y))
            {
                continue;
            }

            var (x, y) = viewport.ToPixel(coordinate.X, coordinate.Y);
            if (!started)
            {
                path.MoveTo(x, y);
                started = true;
            }
            else
            {
                path.LineTo(x, y);
            }
        }

        if (started)
        {
            path.Close();
        }
    }
}
