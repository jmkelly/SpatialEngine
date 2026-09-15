using Spatial.Contracts;
using Spatial.Core.Geometry;

namespace Spatial.Imagery.Vips.Raster;

/// <summary>
/// Pure envelope and pixel-window math for the raster catalogue (ADR-0051).
/// Reprojection delegates to the engine's <see cref="ICoordinateTransforms"/>
/// contract, so no projection algorithm lives here.
/// </summary>
internal static class RasterEnvelopes
{
    /// <summary>
    /// Reprojects an envelope between CRSs by transforming its bounding
    /// polygon. Identity/empty cases return the input unchanged.
    /// </summary>
    public static Envelope Project(
        Envelope envelope, string fromCrs, string toCrs, ICoordinateTransforms transforms, CancellationToken cancellationToken)
    {
        if (envelope.IsEmpty || string.Equals(fromCrs, toCrs, StringComparison.OrdinalIgnoreCase))
        {
            return envelope;
        }

        var source = Parse(fromCrs);
        var target = Parse(toCrs);
        var polygon = GeometryFactory.CreatePolygon(
            [
                new Coordinate(envelope.MinX, envelope.MinY),
                new Coordinate(envelope.MaxX, envelope.MinY),
                new Coordinate(envelope.MaxX, envelope.MaxY),
                new Coordinate(envelope.MinX, envelope.MaxY),
                new Coordinate(envelope.MinX, envelope.MinY),
            ],
            source);
        return transforms.Transform(polygon, source.ToString(), target.ToString(), cancellationToken).Envelope ?? envelope;
    }

    /// <summary>
    /// The integer pixel window of <paramref name="sourceBounds"/> inside a
    /// raster with the given origin extent and pixel size, clamped to the
    /// image's pixel grid. The result is always at least one pixel.
    /// </summary>
    public static (int Left, int Top, int Width, int Height) Window(
        Envelope extent, double pixelSizeX, double pixelSizeY, int imageWidth, int imageHeight, Envelope sourceBounds)
    {
        var left = (int)Math.Floor((sourceBounds.MinX - extent.MinX) / pixelSizeX);
        var top = (int)Math.Floor((extent.MaxY - sourceBounds.MaxY) / pixelSizeY);
        var right = (int)Math.Ceiling((sourceBounds.MaxX - extent.MinX) / pixelSizeX);
        var bottom = (int)Math.Ceiling((extent.MaxY - sourceBounds.MinY) / pixelSizeY);

        left = Math.Clamp(left, 0, imageWidth - 1);
        top = Math.Clamp(top, 0, imageHeight - 1);
        var width = Math.Clamp(right - left, 1, imageWidth - left);
        var height = Math.Clamp(bottom - top, 1, imageHeight - top);
        return (left, top, width, height);
    }

    /// <summary>The pixel coordinate of a point (may fall outside the grid).</summary>
    public static (int X, int Y) Pixel(Envelope extent, double pixelSizeX, double pixelSizeY, double x, double y) =>
        ((int)Math.Floor((x - extent.MinX) / pixelSizeX), (int)Math.Floor((extent.MaxY - y) / pixelSizeY));

    /// <summary>Parses an <c>AUTHORITY:CODE</c> identity, or throws a typed failure.</summary>
    public static CoordinateReference Parse(string crs)
    {
        var colon = crs.IndexOf(':');
        if (colon <= 0 || colon == crs.Length - 1)
        {
            throw SpatialException.BadArguments($"Raster CRS '{crs}' is not a valid authority:code identity.");
        }

        return new CoordinateReference(crs[..colon], crs[(colon + 1)..]);
    }
}
