using SkiaSharp;
using Spatial.Core.Geometry;
using Spatial.PluginSdk;

namespace Spatial.Rendering.Skia.Drawing;

/// <summary>
/// Projects world coordinates (viewport CRS units) to pixels with the y-axis
/// pointing down. The viewport bounds are x-first and the pixel grid is
/// <c>Width × Height</c>.
/// </summary>
internal readonly struct ViewportProjection
{
    private readonly double _minX;
    private readonly double _maxY;
    private readonly double _spanX;
    private readonly double _spanY;
    private readonly double _width;
    private readonly double _height;

    public ViewportProjection(RasterViewport viewport)
    {
        _minX = viewport.Bounds.MinX;
        _maxY = viewport.Bounds.MaxY;
        _spanX = viewport.Bounds.Width;
        _spanY = viewport.Bounds.Height;
        _width = viewport.Width;
        _height = viewport.Height;
    }

    public (float X, float Y) ToPixel(double x, double y) => (
        (float)((x - _minX) / _spanX * _width),
        (float)((_maxY - y) / _spanY * _height));
}

/// <summary>
/// Converts core geometry into Skia paths and pixel points. This is the
/// geometry-to-path conversion boundary; it knows no paints and no canvas.
/// </summary>
internal static class SkiaPathBuilder
{
    public static SKPath BuildFill(IReadOnlyList<IGeometry> geometries, ViewportProjection projection)
    {
        var builder = new SKPathBuilder { FillType = SKPathFillType.EvenOdd };
        foreach (var geometry in geometries)
        {
            AddPolygons(builder, geometry, projection);
        }

        return builder.Detach();
    }

    public static SKPath BuildLine(IReadOnlyList<IGeometry> geometries, ViewportProjection projection)
    {
        var builder = new SKPathBuilder();
        foreach (var geometry in geometries)
        {
            AddLines(builder, geometry, projection);
        }

        return builder.Detach();
    }

    /// <summary>Projects every point in a point geometry (single, multi or collection).</summary>
    public static IEnumerable<(float X, float Y)> Points(IReadOnlyList<IGeometry> geometries, ViewportProjection projection)
    {
        foreach (var geometry in geometries)
        {
            foreach (var coordinate in Points(geometry))
            {
                yield return projection.ToPixel(coordinate.X, coordinate.Y);
            }
        }
    }

    private static void AddPolygons(SKPathBuilder path, IGeometry geometry, ViewportProjection projection)
    {
        switch (geometry)
        {
            case IPolygon polygon:
                AddRing(path, polygon.ExteriorRing, projection, close: true);
                foreach (var hole in polygon.InteriorRings)
                {
                    AddRing(path, hole, projection, close: true);
                }

                break;
            case IMultiPolygon multi:
                foreach (var child in multi.Polygons)
                {
                    AddPolygons(path, child, projection);
                }

                break;
            case IGeometryParts parts:
                foreach (var child in parts.Geometries)
                {
                    AddPolygons(path, child, projection);
                }

                break;
        }
    }

    private static void AddLines(SKPathBuilder path, IGeometry geometry, ViewportProjection projection)
    {
        switch (geometry)
        {
            case ILineString line:
                AddRing(path, line, projection, close: false);
                break;
            case IMultiLineString multi:
                foreach (var child in multi.LineStrings)
                {
                    AddRing(path, child, projection, close: false);
                }

                break;
            case IGeometryParts parts:
                foreach (var child in parts.Geometries)
                {
                    AddLines(path, child, projection);
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

    private static void AddRing(SKPathBuilder path, ILineString ring, ViewportProjection projection, bool close)
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

            var (x, y) = projection.ToPixel(coordinate.X, coordinate.Y);
            if (started)
            {
                path.LineTo(x, y);
            }
            else
            {
                path.MoveTo(x, y);
                started = true;
            }
        }

        if (started && close)
        {
            path.Close();
        }
    }
}
