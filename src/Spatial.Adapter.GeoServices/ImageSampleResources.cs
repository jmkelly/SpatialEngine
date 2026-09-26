using Spatial.Contracts;
using Spatial.Core.Geometry;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The ImageService resources that render a preview of the whole dataset: the
/// Legend swatch set (S3 legend-image-service/) and the service-level
/// Thumbnail (spec §8.3). Both reduce the dataset through the render engine
/// and both answer either encoded image bytes or a JSON href, so they share
/// the frame-and-serve step and differ only in the swatch grid they build.
/// Split out of <see cref="ImageServerEndpoints"/> — which maps the routes —
/// so the preview fan-out lives with the previews.
/// </summary>
internal static class ImageSampleResources
{
    /// <summary>The square side of a legend swatch (the reference serves 20x20 symbols).</summary>
    private const int LegendSwatchSize = 20;

    /// <summary>
    /// The Legend resource (S3 legend-image-service/): one entry per band
    /// with the 20x20 dataset render as the swatch. The render is what
    /// <c>exportImage</c> serves; <c>renderingRule</c>/<c>variable</c> would
    /// change the symbology and are rejected by name.
    /// </summary>
    internal static async Task<IResult> Legend(ImageServerRequest request)
    {
        try
        {
            var scope = await ImageServerScope.OpenAsync(request);
            ImageFileHandlers.RejectExportParameter(scope.Parameters, "renderingRule", "raster functions are not supported.");
            ImageFileHandlers.RejectExportParameter(scope.Parameters, "variable", "multidimensional variables are not supported.");
            var image = await scope.ImageAsync();
            var info = image.Description.Raster;
            var bandIds = ImageLegendBuilder.ParseLegendBandIds(scope.Parameter("bandIds"), info.BandCount);
            if (info.Extent.IsEmpty)
            {
                throw GeoServicesErrors.Invalid($"Image Service '{request.Service}' has no extent to render a legend from.");
            }

            var viewport = new RasterViewport(info.Extent, LegendSwatchSize, LegendSwatchSize, info.Crs);
            var exported = await image.Catalogue.ExportAsync(
                image.Dataset, new RasterExportRequest(viewport, RasterFormat.Png), request.CancellationToken);
            return EsriJson.Value(ImageLegendBuilder.Legend(image.Description, exported.Content, exported.Width, exported.Height, bandIds));
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    /// <summary>
    /// The service-level Thumbnail: the whole dataset reduced like one
    /// catalog item's thumbnail (spec §8.3), streamed or as a JSON href.
    /// </summary>
    internal static async Task<IResult> Thumbnail(ImageServerRequest request)
    {
        try
        {
            // The thumbnail answers both the streamed image and the JSON href,
            // so it negotiates the format itself rather than through the scope.
            var scope = await ImageServerScope.ReadAsync(request);
            var format = scope.Parameter("f");
            var stream = string.IsNullOrWhiteSpace(format) || string.Equals(format, "image", StringComparison.OrdinalIgnoreCase);
            if (!stream)
            {
                EsriFormat.Ensure(format);
            }

            var image = await scope.ImageAsync();
            var viewport = ImageService.ThumbnailViewport(image.Description.Raster, ImageFileHandlers.ThumbnailMaxSize);
            var exported = await image.Catalogue.ExportAsync(
                image.Dataset, new RasterExportRequest(viewport, RasterFormat.Png), request.CancellationToken);
            if (stream)
            {
                GeoServicesResponses.WriteImageHeaders(request.Context, exported);
                return Results.Bytes(exported.Content, exported.MediaType);
            }

            return EsriJson.Value(ImageService.Export(
                GeoServicesResponses.ExportHref(request.Context), viewport, MapServerResources.SridOf(image.Description.Raster.Crs)));
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }
}
