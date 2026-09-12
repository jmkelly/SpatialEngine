using Spatial.PluginSdk;

namespace Spatial.Rendering.Skia.Pipeline;

/// <summary>
/// Estimates the integer-ish Web-Mercator zoom implied by a viewport's
/// resolution, so style zoom windows can be applied. Only EPSG:3857 and
/// EPSG:4326 are mapped; an unknown CRS yields 0 (no zoom filtering).
/// </summary>
internal static class ZoomEstimator
{
    private const double MercatorInitialResolution = 156543.03392804097;

    public static double ZoomLevel(RasterViewport viewport)
    {
        if (viewport.Width <= 0)
        {
            return 0;
        }

        var resolution = viewport.Bounds.Width / viewport.Width;
        if (resolution <= 0)
        {
            return 0;
        }

        if (CrsIs(viewport.Crs, "3857"))
        {
            return Math.Log2(MercatorInitialResolution / resolution);
        }

        return CrsIs(viewport.Crs, "4326") ? Math.Log2(360.0 / (256.0 * resolution)) : 0;
    }

    private static bool CrsIs(string crs, string code) =>
        crs.EndsWith(FormattableString.Invariant($":{code}"), StringComparison.OrdinalIgnoreCase)
        || string.Equals(crs, code, StringComparison.OrdinalIgnoreCase);
}
