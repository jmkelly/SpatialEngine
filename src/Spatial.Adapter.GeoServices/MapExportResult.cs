using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Geometry;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The two shapes the MapServer export route answers with (spec §4.0.4):
/// <c>f=image</c> streams the rendered bytes, anything else answers the Esri
/// export metadata envelope describing the frame that was exported. Split
/// out of <see cref="MapExportEndpoints"/> — which only chooses between
/// them — so the response projection, including the extent's spatial
/// reference, is written once.
/// </summary>
internal static class MapExportResult
{
    /// <summary>Streams the rendered image bytes with the image response headers.</summary>
    public static IResult Image(HttpContext context, RasterImage image)
    {
        GeoServicesResponses.WriteImageHeaders(context, image);
        return Results.Bytes(image.Content, image.MediaType);
    }

    /// <summary>
    /// The export metadata envelope: the href the image would be served at,
    /// the exported size, the exported extent in <c>imageSR</c> and the
    /// scale denominator for the requested <c>dpi</c>.
    /// </summary>
    public static IResult Metadata(HttpContext context, MapExportPlan plan, double dpi) =>
        EsriJson.Value(new EsriMapExportResponse(
            GeoServicesResponses.ExportHref(context),
            plan.Width,
            plan.Height,
            ExportExtent(plan.Viewport.Bounds, plan.ImageCrs),
            plan.Viewport.Bounds.Width / plan.Width * dpi / 0.0254));

    private static EsriExtent ExportExtent(Envelope bounds, CoordinateReference? crs) =>
        new(bounds.MinX, bounds.MinY, bounds.MaxX, bounds.MaxY,
            EsriLayerModel.SpatialReference(MapServerResources.SridOf(crs?.ToString() ?? string.Empty)));
}
