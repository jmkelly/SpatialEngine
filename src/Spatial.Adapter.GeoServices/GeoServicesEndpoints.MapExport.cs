using Microsoft.Extensions.Logging;
using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Geometry;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The Map Server export route (spec §4.0.4, ADR-0048). The tile route
/// lives with <see cref="MapServerTileEndpoints"/> so this facade keeps
/// only the export fan-out. One structured event per request carries the
/// service and the request values (ADR-0045), so a blank parity panel is
/// diagnosable from the log alone.
/// </summary>
internal static partial class MapExportEndpoints
{
    internal static void MapExportRoutes(RouteGroupBuilder group, GeoServicesCatalog catalog, IMapRegistry registry)
    {
        group.MapMethods("/{service}/MapServer/export", ["GET", "POST"], (
            string service, HttpContext context, IStoreRegistry stores, IMapRenderer renderer,
            ICoordinateTransforms transforms, ILoggerFactory loggerFactory, CancellationToken cancellationToken) =>
            MapExport(catalog, registry, service, context, stores, renderer, transforms, loggerFactory, cancellationToken));
        MapServerTileEndpoints.MapTileRoutes(group, catalog, registry);
    }

    private static async Task<IResult> MapExport(
        GeoServicesCatalog catalog, IMapRegistry registry, string service, HttpContext context, IStoreRegistry stores,
        IMapRenderer renderer, ICoordinateTransforms transforms, ILoggerFactory loggerFactory, CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger(typeof(MapExportEndpoints));
        EsriRequestParameters? parameters = null;
        try
        {
            parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            var resolved = await GeoServicesResolution.ResolveServiceAsync(catalog, registry, service, "MapServer", MapServiceKind.MapServer, cancellationToken);
            var effective = MapDynamicLayers.Apply(
                await GeoServicesResolution.ListLayersAsync(stores, resolved, cancellationToken),
                MapDynamicLayers.Parse(parameters.Get("dynamicLayers")),
                service);
            var selected = MapLayerSelection.Select(effective, parameters.Get("layers"), parameters.Get("layerOption"));
            var time = EsriFeatureQuery.ParseTime(parameters.Get("time"));
            _ = MapExportTime.ParseTimeRelation(parameters.Get("timeRelation"));
            var times = MapExportTime.ResolveTimes(
                selected, time, MapExportTime.ParseLayerTimeOptions(parameters.Get("layerTimeOptions")));
            var bbox = MapRenderEngine.ParseBbox(parameters.Get("bbox"));
            var (width, height) = MapRenderEngine.ParseSize(parameters.Get("size"));
            var bboxCrs = EsriValueParser.ParseSpatialReference(parameters.Get("bboxSR")) ?? await MapCrsAsync(stores, resolved, selected, cancellationToken);
            var imageCrs = EsriValueParser.ParseSpatialReference(parameters.Get("imageSR")) ?? bboxCrs;
            var viewport = new RasterViewport(
                MapRenderEngine.Project(bbox, bboxCrs, imageCrs ?? bboxCrs, transforms, cancellationToken),
                width,
                height,
                (imageCrs ?? bboxCrs)?.ToString() ?? "EPSG:4326");
            var style = MapRenderEngine.Style(service, selected);
            var sources = MapRenderEngine.Sources(stores, resolved.Store, selected, MapRenderEngine.ParseLayerDefs(parameters.Get("layerDefs")), times);
            var format = MapRenderEngine.ParseFormat(parameters.Get("format"));
            var request = new MapRenderRequest(viewport, style, sources, null, format, 90, null, parameters.GetBool("transparent", true), 1.0);
            if (string.Equals(parameters.Get("f"), "image", StringComparison.OrdinalIgnoreCase))
            {
                var image = await renderer.RenderAsync(request, cancellationToken);
                if (logger.IsEnabled(LogLevel.Information))
                {
                    var values = GeoServicesExportLogging.FormatParameters(parameters);
                    GeoServicesExportLogging.LogMapExportCompleted(
                        logger, context.Request.Method, context.Request.Path.Value ?? "/",
                        service, image.Width, image.Height, image.Content.LongLength, image.MediaType,
                        values);
                }

                GeoServicesResponses.WriteImageHeaders(context, image);
                return Results.Bytes(image.Content, image.MediaType);
            }

            var dpi = MapRenderEngine.Dpi(parameters.Get("dpi"));
            if (logger.IsEnabled(LogLevel.Information))
            {
                var values = GeoServicesExportLogging.FormatParameters(parameters);
                GeoServicesExportLogging.LogMapExportCompleted(
                    logger, context.Request.Method, context.Request.Path.Value ?? "/",
                    service, width, height, 0, "application/json",
                    values);
            }

            return EsriJson.Value(new EsriMapExportResponse(
                GeoServicesResponses.ExportHref(context),
                width,
                height,
                ExportExtent(viewport.Bounds, imageCrs ?? bboxCrs),
                viewport.Bounds.Width / width * dpi / 0.0254));
        }
        catch (Exception exception)
        {
            if (exception is not OperationCanceledException && logger.IsEnabled(LogLevel.Warning))
            {
                var failure = EsriErrorMapper.Describe(exception);
                var values = GeoServicesExportLogging.FormatParameters(parameters);
                GeoServicesExportLogging.LogMapExportFailed(
                    logger, exception, context.Request.Method, context.Request.Path.Value ?? "/",
                    service, failure.EsriCode, failure.HttpStatus, failure.Message,
                    values);
            }

            return EsriErrorMapper.Map(exception);
        }
    }

    private static async Task<CoordinateReference?> MapCrsAsync(
        IStoreRegistry stores, ResolvedService resolved, IReadOnlyList<PublishedLayer> layers, CancellationToken cancellationToken)
    {
        if (layers.Count == 0)
        {
            return null;
        }

        var description = await MapServerEndpoints.Catalogue(stores, resolved.Store).DescribeAsync(layers[0].Dataset, cancellationToken);
        return EsriLayerModel.LayerCoordinateReference(description.Srid);
    }

    private static EsriExtent ExportExtent(Envelope bounds, CoordinateReference? crs) =>
        new(bounds.MinX, bounds.MinY, bounds.MaxX, bounds.MaxY, EsriLayerModel.SpatialReference(MapServerResources.SridOf(crs?.ToString() ?? string.Empty)));
}
