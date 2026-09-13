using System.Globalization;
using Spatial.PluginSdk;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The response shapes shared by the Map Service and Image Service routes:
/// the raster metadata headers and the export href that point an
/// <c>f=json</c> export at its <c>f=image</c> bytes.
/// </summary>
internal static class GeoServicesResponses
{
    public static void WriteImageHeaders(HttpContext context, RasterImage image)
    {
        context.Response.Headers["X-Raster-Width"] = image.Width.ToString(CultureInfo.InvariantCulture);
        context.Response.Headers["X-Raster-Height"] = image.Height.ToString(CultureInfo.InvariantCulture);
        context.Response.Headers["X-Raster-Format"] = image.Format.ToString();
    }

    public static string ExportHref(HttpContext context)
    {
        var query = context.Request.Query.ToDictionary(pair => pair.Key, pair => (string?)pair.Value.ToString());
        query["f"] = "image";
        return $"{context.Request.Scheme}://{context.Request.Host}{context.Request.Path}{QueryString.Create(query)}";
    }
}
