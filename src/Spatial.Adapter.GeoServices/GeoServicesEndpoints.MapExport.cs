using Microsoft.Extensions.Logging;
using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The Map Server export route (spec §4.0.4, ADR-0048). The tile route
/// lives with <see cref="MapServerTileEndpoints"/> so this facade keeps
/// only the export fan-out. One structured event per request carries the
/// service and the request values (ADR-0045), so a blank parity panel is
/// diagnosable from the log alone. The export itself is planned by
/// <see cref="MapExportPlanner"/> and answered by
/// <see cref="MapExportResult"/>, leaving this type the routing and the
/// Esri error envelope.
/// </summary>
internal static partial class MapExportEndpoints
{
    internal static void MapExportRoutes(RouteGroupBuilder group, GeoServicesCatalog catalog, IMapRegistry registry)
    {
        group.MapMethods("/{service}/MapServer/export", ["GET", "POST"], (
            string service, HttpContext context, IStoreRegistry stores, IMapRenderer renderer,
            ICoordinateTransforms transforms, ILoggerFactory loggerFactory, CancellationToken cancellationToken) =>
            MapExport(new ExportRoute(catalog, registry, service, context, stores, renderer, transforms, loggerFactory, cancellationToken)));
        MapServerTileEndpoints.MapTileRoutes(group, catalog, registry);
    }

    /// <summary>One export request's seams: the published service, its stores and the rendering stack.</summary>
    private sealed record ExportRoute(
        GeoServicesCatalog Catalog,
        IMapRegistry Registry,
        string Service,
        HttpContext Context,
        IStoreRegistry Stores,
        IMapRenderer Renderer,
        ICoordinateTransforms Transforms,
        ILoggerFactory LoggerFactory,
        CancellationToken CancellationToken);

    private static async Task<IResult> MapExport(ExportRoute route)
    {
        var (catalog, registry, service, context, stores, renderer, transforms, loggerFactory, cancellationToken) = route;
        var logger = loggerFactory.CreateLogger(typeof(MapExportEndpoints));
        EsriRequestParameters? parameters = null;
        try
        {
            parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            var layers = await MapExportLayers.ResolveAsync(catalog, registry, stores, service, parameters, cancellationToken);
            var plan = await MapExportPlanner.CreateAsync(layers, stores, service, parameters, transforms, cancellationToken);
            if (string.Equals(parameters.Get("f"), "image", StringComparison.OrdinalIgnoreCase))
            {
                var request = new MapRenderRequest(
                    plan.Viewport, plan.Style, plan.Sources, null, plan.Format, 90, null,
                    parameters.GetBool("transparent", false), 1.0);
                var image = await renderer.RenderAsync(request, cancellationToken);
                if (logger.IsEnabled(LogLevel.Information))
                {
                    var values = GeoServicesExportLogging.FormatParameters(parameters);
                    GeoServicesExportLogging.LogMapExportCompleted(
                        logger, context.Request.Method, context.Request.Path.Value ?? "/",
                        service, image.Width, image.Height, image.Content.LongLength, image.MediaType,
                        values);
                }

                return MapExportResult.Image(context, image);
            }

            if (logger.IsEnabled(LogLevel.Information))
            {
                var values = GeoServicesExportLogging.FormatParameters(parameters);
                GeoServicesExportLogging.LogMapExportCompleted(
                    logger, context.Request.Method, context.Request.Path.Value ?? "/",
                    service, plan.Width, plan.Height, 0, "application/json",
                    values);
            }

            return MapExportResult.Metadata(context, plan, MapRenderParameters.Dpi(parameters.Get("dpi")));
        }
        catch (Exception exception)
        {
            if (exception is not OperationCanceledException && logger.IsEnabled(LogLevel.Warning))
            {
                var failure = EsriErrorMapper.Describe(exception);
                GeoServicesExportLogging.LogMapExportFailed(
                    logger, exception, context.Request.Method, context.Request.Path.Value ?? "/",
                    service, failure.EsriCode, failure.HttpStatus, failure.Message,
                    GeoServicesExportLogging.FormatParameters(parameters));
            }

            return EsriErrorMapper.Map(exception);
        }
    }

}
