using Spatial.Core.Geometry;
using Spatial.Interop.Esri;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Providers;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The Map Server export and tile routes (spec §4.0.4, §4.8, ADR-0048). They
/// share the rendering seam (<see cref="IMapRenderer"/>) and the tile scheme
/// lookup, so they live together and apart from the metadata/query routes.
/// </summary>
internal static class MapExportEndpoints
{
    internal static void MapExportRoutes(RouteGroupBuilder group, GeoServicesCatalog catalog, IMapRegistry registry)
    {
        group.MapMethods("/{service}/MapServer/export", ["GET", "POST"], (
            string service, HttpContext context, IServiceProvider services, ICoordinateTransforms transforms, CancellationToken cancellationToken) =>
            MapExport(catalog, registry, service, context, services, transforms, cancellationToken));
        group.MapMethods("/{service}/MapServer/tile/{z:int}/{y:int}/{x:int}", ["GET", "POST"], (
            string service, int z, int y, int x, HttpContext context, IServiceProvider services, CancellationToken cancellationToken) =>
            MapTile(catalog, registry, service, z, y, x, context, services, cancellationToken));
    }

    private static async Task<IResult> MapExport(
        GeoServicesCatalog catalog, IMapRegistry registry, string service, HttpContext context, IServiceProvider services,
        ICoordinateTransforms transforms, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            var resolved = await GeoServicesEndpoints.ResolveServiceAsync(catalog, registry, service, "MapServer", MapService.Map, cancellationToken);
            var selected = MapLayerSelection.Select(await GeoServicesEndpoints.ListLayersAsync(services, resolved, cancellationToken), parameters.Get("layers"));
            var bbox = MapRenderEngine.ParseBbox(parameters.Get("bbox"));
            var (width, height) = MapRenderEngine.ParseSize(parameters.Get("size"));
            var bboxCrs = EsriValueParser.ParseSpatialReference(parameters.Get("bboxSR")) ?? await MapCrsAsync(services, resolved, selected, cancellationToken);
            var imageCrs = EsriValueParser.ParseSpatialReference(parameters.Get("imageSR")) ?? bboxCrs;
            var viewport = new RasterViewport(
                MapRenderEngine.Project(bbox, bboxCrs, imageCrs ?? bboxCrs, transforms, cancellationToken),
                width,
                height,
                (imageCrs ?? bboxCrs)?.ToString() ?? "EPSG:4326");
            var style = MapRenderEngine.Style(service, selected);
            var sources = MapRenderEngine.Sources(services, resolved.Store, selected, MapRenderEngine.ParseLayerDefs(parameters.Get("layerDefs")));
            var format = MapRenderEngine.ParseFormat(parameters.Get("format"));
            var request = new MapRenderRequest(viewport, style, sources, null, format, 90, null, parameters.GetBool("transparent", true), 1.0);
            if (string.Equals(parameters.Get("f"), "image", StringComparison.OrdinalIgnoreCase))
            {
                var image = await Renderer(services).RenderAsync(request, cancellationToken);
                GeoServicesResponses.WriteImageHeaders(context, image);
                return Results.Bytes(image.Content, image.MediaType);
            }

            var dpi = MapRenderEngine.Dpi(parameters.Get("dpi"));
            return EsriJson.Value(new EsriMapExportResponse(
                GeoServicesResponses.ExportHref(context),
                width,
                height,
                ExportExtent(viewport.Bounds, imageCrs ?? bboxCrs),
                viewport.Bounds.Width / width * dpi / 0.0254));
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    private static async Task<IResult> MapTile(
        GeoServicesCatalog catalog, IMapRegistry registry, string service, int z, int y, int x,
        HttpContext context, IServiceProvider services, CancellationToken cancellationToken)
    {
        try
        {
            var resolved = await GeoServicesEndpoints.ResolveServiceAsync(catalog, registry, service, "MapServer", MapService.Map, cancellationToken);
            var layers = await GeoServicesEndpoints.ListLayersAsync(services, resolved, cancellationToken);
            var scheme = MapServerEndpoints.MapTileScheme(services)
                ?? throw new EsriInteropException(EsriErrorCodes.ServiceUnavailable, "No tiling scheme is configured on this host.");
            var coordinate = new TileCoordinate(z, x, y);
            if (!scheme.IsValid(coordinate))
            {
                throw new EsriInteropException(EsriErrorCodes.NotFound, $"Tile {z}/{y}/{x} is outside the tiling scheme.");
            }

            var style = MapRenderEngine.Style(service, layers);
            var cache = services.GetService<ITileCache>();
            var key = new TileCacheKey(scheme.Id, z, x, y, RasterFormat.Png, MapRenderEngine.Version(service, style));
            var image = cache is not null ? await cache.TryGetAsync(key, cancellationToken) : null;
            if (image is null)
            {
                var viewport = new RasterViewport(scheme.Bounds(coordinate), scheme.TileSize, scheme.TileSize, scheme.Crs);
                var sources = MapRenderEngine.Sources(services, resolved.Store, layers, null);
                image = await Renderer(services).RenderAsync(
                    new MapRenderRequest(viewport, style, sources, null, RasterFormat.Png, 90, null, true, 1.0), cancellationToken);
                if (cache is not null)
                {
                    await cache.SetAsync(key, image, cancellationToken);
                }
            }

            GeoServicesResponses.WriteImageHeaders(context, image);
            return Results.Bytes(image.Content, image.MediaType);
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    private static IMapRenderer Renderer(IServiceProvider services) =>
        services.GetService<IMapRenderer>()
        ?? throw new EsriInteropException(EsriErrorCodes.ServiceUnavailable, "Rendering is not configured on this host.");

    private static async Task<CoordinateReference?> MapCrsAsync(
        IServiceProvider services, ResolvedService resolved, IReadOnlyList<PublishedLayer> layers, CancellationToken cancellationToken)
    {
        if (layers.Count == 0)
        {
            return null;
        }

        var description = await MapServerEndpoints.Catalogue(services, resolved.Store).DescribeAsync(layers[0].Dataset, cancellationToken);
        return EsriLayerModel.LayerCoordinateReference(description.Srid);
    }

    private static EsriExtent ExportExtent(Envelope bounds, CoordinateReference? crs) =>
        new(bounds.MinX, bounds.MinY, bounds.MaxX, bounds.MaxY, EsriLayerModel.SpatialReference(MapServerResources.SridOf(crs?.ToString() ?? string.Empty)));
}
