namespace RenderSpike;

using Spatial.Core.Geometry;

/// <summary>
/// A rendering viewport: a Web Mercator bounding box (metres, x-first) plus a
/// pixel size. This is the spike's stand-in for the real SDK viewport DTO.
/// </summary>
public readonly record struct Viewport(double MinX, double MinY, double MaxX, double MaxY, int Width, int Height)
{
    public static Viewport FromLonLat(double west, double south, double east, double north, int width, int height)
    {
        var (x0, y0) = WebMercator.FromLonLat(west, south);
        var (x1, y1) = WebMercator.FromLonLat(east, north);
        return new(x0, y0, x1, y1, width, height);
    }

    /// <summary>World units (metres) covered by one pixel; the natural simplify tolerance.</summary>
    public double UnitsPerPixel => (MaxX - MinX) / Width;

    /// <summary>Projects a world coordinate (metres) to a pixel with the y-axis pointing down.</summary>
    public (float X, float Y) ToPixel(double x, double y) => (
        (float)((x - MinX) / (MaxX - MinX) * Width),
        (float)((MaxY - y) / (MaxY - MinY) * Height));
}

/// <summary>EPSG:4326 lon/lat to EPSG:3857 metres (spherical Web Mercator).</summary>
public static class WebMercator
{
    private const double EarthRadius = 6378137.0;

    public static (double X, double Y) FromLonLat(double lon, double lat)
    {
        var x = EarthRadius * lon * Math.PI / 180.0;
        var y = EarthRadius * Math.Log(Math.Tan((Math.PI / 4.0) + (lat * Math.PI / 360.0)));
        return (x, y);
    }
}
