using Spatial.Contracts;
using Spatial.Core.Geometry;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The ImageService resources that summarise the raster itself rather than
/// selecting catalog items (spec §8, ADR-0051/0054): the stored band
/// <c>statistics</c>, <c>computeHistograms</c> and the
/// <c>rasterAttributeTable</c>. Split out of
/// <see cref="ImageServerEndpoints"/> — which maps the routes — so the
/// per-band and per-value summaries carry their own fan-out; the
/// item-selection resources are <see cref="ImageQueryResources"/>.
/// </summary>
internal static class ImageBandResources
{
    /// <summary>
    /// The stored band statistics resource (S3 statistics/): the dataset's
    /// configured statistics, or a typed <c>not.found</c> when the dataset
    /// carries none. Computation is the <c>computeHistograms</c> path.
    /// </summary>
    internal static async Task<IResult> Statistics(ImageServerRequest request)
    {
        try
        {
            var scope = await ImageServerScope.OpenAsync(request);
            var image = await scope.ImageAsync();
            var statistics = image.Description.Raster.BandStatistics;
            if (statistics is not { Count: > 0 })
            {
                throw GeoServicesErrors.NotFound($"Image Service '{request.Service}' has no stored band statistics.");
            }

            return EsriJson.Value(ImageService.Statistics(statistics));
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    /// <summary>
    /// The Compute Histograms operation (spec §8): per-band histograms over
    /// the requested envelope or polygon. Mosaic, rendering, pixel-size,
    /// time and multidimensional selectors would change the pixels and are
    /// rejected by name; the provider projects and clips to the raster.
    /// </summary>
    internal static async Task<IResult> ComputeHistograms(ImageServerRequest request)
    {
        try
        {
            var scope = await ImageServerScope.OpenAsync(request);
            RequireGeometryType(scope.Parameters.Require("geometryType"));
            ImageFileHandlers.RejectExportParameter(scope.Parameters, "mosaicRule", "on-the-fly mosaicking is not supported.");
            ImageFileHandlers.RejectExportParameter(scope.Parameters, "renderingRule", "raster functions are not supported.");
            ImageFileHandlers.RejectExportParameter(scope.Parameters, "pixelSize", "histograms are computed at the base resolution.");
            ImageFileHandlers.RejectExportParameter(scope.Parameters, "time", "the raster catalog carries no temporal dimension.");
            ImageFileHandlers.RejectExportParameter(scope.Parameters, "processAsMultidimensional", "multidimensional rasters are not supported.");
            var image = await scope.ImageAsync();
            var rasterCrs = image.Description.Raster.Crs;
            var geometry = EsriValueParser.ParseGeometry(
                scope.Parameters.Require("geometry"), CoordinateReference.Epsg(MapServerResources.SridOf(rasterCrs)));
            var bounds = geometry.Envelope ?? Envelope.Empty;
            if (bounds.IsEmpty)
            {
                throw GeoServicesErrors.Invalid("The 'geometry' parameter must cover a non-empty area.");
            }

            var histograms = await image.Catalogue.ComputeHistogramsAsync(
                image.Dataset,
                new RasterHistogramRequest(bounds, geometry.CoordinateReference?.ToString() ?? rasterCrs),
                request.CancellationToken);
            return EsriJson.Value(ImageService.Histograms(histograms));
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    /// <summary>
    /// The raster attribute table resource (S3 raster-attribute-table/): the
    /// configured value-frequency table, or a typed <c>not.found</c> when
    /// the dataset carries none (the resource exists only if the raster has
    /// a table, like the reference).
    /// </summary>
    internal static async Task<IResult> AttributeTable(ImageServerRequest request)
    {
        try
        {
            var scope = await ImageServerScope.OpenAsync(request);
            ImageFileHandlers.RejectExportParameter(scope.Parameters, "renderingRule", "raster functions are not supported.");
            var image = await scope.ImageAsync();
            var table = image.Description.Raster.AttributeTable
                ?? throw GeoServicesErrors.NotFound($"Image Service '{request.Service}' does not have a raster attribute table.");
            return ImageService.AttributeTable(table);
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    /// <summary>Only the two geometry kinds histograms are computed over are accepted; anything else is invalid.</summary>
    private static void RequireGeometryType(string geometryType)
    {
        if (!string.Equals(geometryType, "esriGeometryEnvelope", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(geometryType, "esriGeometryPolygon", StringComparison.OrdinalIgnoreCase))
        {
            throw GeoServicesErrors.Invalid(
                $"'geometryType' must be esriGeometryEnvelope or esriGeometryPolygon, got '{geometryType}'.");
        }
    }
}
