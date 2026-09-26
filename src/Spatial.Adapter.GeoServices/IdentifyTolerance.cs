using System.Globalization;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The MapServer identify pixel tolerance in map units (spec §4.0.5):
/// <c>tolerance</c> pixels scaled by the map's units-per-pixel from
/// <c>mapExtent</c>/<c>imageDisplay</c>. Zero when the client supplies
/// neither (an exact intersection test) or supplies a frame that cannot
/// yield a scale. Split out of <see cref="MapIdentifyEngine"/> so the
/// pixel-to-map-unit arithmetic is named on its own.
/// </summary>
internal static class IdentifyTolerance
{
    /// <summary>The tolerance in map units; zero for an exact intersection test.</summary>
    public static double Units(EsriRequestParameters parameters)
    {
        var pixels = ParseDouble(parameters.Get("tolerance"), 3.0);
        var mapExtent = parameters.Get("mapExtent");
        var imageDisplay = parameters.Get("imageDisplay");
        if (mapExtent is null || imageDisplay is null)
        {
            return 0;
        }

        var extent = EsriValueParser.ParseDoubles(mapExtent, "mapExtent");
        var display = EsriValueParser.ParseDoubles(imageDisplay, "imageDisplay");
        return TryUnitsPerPixel(extent, display, out var unitsPerPixel)
            ? Math.Abs(pixels) * unitsPerPixel
            : 0;
    }

    private static bool TryUnitsPerPixel(IReadOnlyList<double> extent, IReadOnlyList<double> display, out double unitsPerPixel)
    {
        unitsPerPixel = 0;
        if (extent.Count < 4 || display.Count < 2 || display[0] <= 0)
        {
            return false;
        }

        unitsPerPixel = (extent[2] - extent[0]) / display[0];
        return true;
    }

    private static double ParseDouble(string? value, double fallback) =>
        string.IsNullOrWhiteSpace(value) || !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? fallback
            : parsed;
}
