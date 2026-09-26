using Microsoft.Extensions.Logging;
using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The ImageService root and <c>exportImage</c> resources (spec §8,
/// ADR-0051): the served metadata document and the rendered image response,
/// including the whole-dataset render the per-item image resource reuses.
/// Split out of <see cref="ImageServerEndpoints"/> — which maps the routes —
/// so each class carries its own fan-out.
/// </summary>
internal static class ImageServerRootResources
{
    /// <summary>The Image Service root: the served metadata document (spec §8.1).</summary>
    internal static async Task<IResult> Root(ImageServerRequest request, GeoServicesOptions options)
    {
        try
        {
            var scope = await ImageServerScope.OpenAsync(request);
            var image = await scope.ImageAsync();
            return EsriJson.Value(ImageService.Root(image.Description, image.Copyright, options));
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    /// <summary>The <c>exportImage</c> resource (spec §8.4): the requested frame as encoded image bytes.</summary>
    internal static async Task<IResult> Export(
        ImageServerRequest request, ICoordinateTransforms transforms, ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger(typeof(ImageServerRootResources));
        EsriRequestParameters? parameters = null;
        try
        {
            var scope = await ImageServerScope.ReadAsync(request);
            var image = await scope.ImageAsync();
            var rasterId = ImageFileHandlers.ParseExportRasterId(scope.Parameters, image.Description, request.Service);
            var result = await ImageFileHandlers.ExportImageAsync(
                new(image, scope.Parameters, transforms, rasterId, DefaultBbox: null, DefaultCrs: null),
                request.Context, request.CancellationToken);
            if (logger.IsEnabled(LogLevel.Information))
            {
                var values = GeoServicesExportLogging.FormatParameters(parameters ?? scope.Parameters);
                GeoServicesExportLogging.LogImageExportCompleted(
                    logger, request.Context.Request.Method, request.Context.Request.Path.Value ?? "/",
                    request.Service, values);
            }

            return result;
        }
        catch (Exception exception)
        {
            if (exception is not OperationCanceledException && logger.IsEnabled(LogLevel.Warning))
            {
                var failure = EsriErrorMapper.Describe(exception);
                var values = GeoServicesExportLogging.FormatParameters(parameters);
                GeoServicesExportLogging.LogImageExportFailed(
                    logger, exception, request.Context.Request.Method, request.Context.Request.Path.Value ?? "/",
                    request.Service, failure.EsriCode, failure.HttpStatus, failure.Message,
                    values);
            }

            return EsriErrorMapper.Map(exception);
        }
    }
}
