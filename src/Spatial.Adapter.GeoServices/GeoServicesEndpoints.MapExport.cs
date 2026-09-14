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
            string service, HttpContext context, IStoreRegistry stores, IMapRenderer renderer,
            ICoordinateTransforms transforms, CancellationToken cancellationToken) =>
            MapExport(catalog, registry, service, context, stores, renderer, transforms, cancellationToken));
        group.MapMethods("/{service}/MapServer/tile/{z:int}/{y:int}/{x:int}", ["GET", "POST"], (
            string service, [AsParameters] MapTileAddress address, HttpContext context, IStoreRegistry stores,
            IMapRenderer renderer, ITileCache cache, IEnumerable<ITileScheme> schemes, CancellationToken cancellationToken) =>
            MapTile(catalog, registry, service, address, context, stores,
                new MapTileRenderDependencies(renderer, cache, [.. schemes]), cancellationToken));
    }

    private static async Task<IResult> MapExport(
        GeoServicesCatalog catalog, IMapRegistry registry, string service, HttpContext context, IStoreRegistry stores,
        IMapRenderer renderer, ICoordinateTransforms transforms, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            var resolved = await GeoServicesEndpoints.ResolveServiceAsync(catalog, registry, service, "MapServer", MapService.Map, cancellationToken);
            var effective = MapDynamicLayers.Apply(
                await GeoServicesEndpoints.ListLayersAsync(stores, resolved, cancellationToken),
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
        GeoServicesCatalog catalog, IMapRegistry registry, string service, MapTileAddress address,
        HttpContext context, IStoreRegistry stores, MapTileRenderDependencies render, CancellationToken cancellationToken)
    {
        try
        {
            var resolved = await GeoServicesEndpoints.ResolveServiceAsync(catalog, registry, service, "MapServer", MapService.Map, cancellationToken);
            var layers = await GeoServicesEndpoints.ListLayersAsync(stores, resolved, cancellationToken);
            var scheme = MapServerEndpoints.MapTileScheme(render.Schemes)
                ?? throw new EsriInteropException(EsriErrorCodes.ServiceUnavailable, "No tiling scheme is configured on this host.");
            var coordinate = new TileCoordinate(address.Z, address.X, address.Y);
            if (!scheme.IsValid(coordinate))
            {
                throw new EsriInteropException(EsriErrorCodes.NotFound, $"Tile {address.Z}/{address.Y}/{address.X} is outside the tiling scheme.");
            }

            var style = MapRenderEngine.Style(service, layers);
            var key = new TileCacheKey(scheme.Id, address.Z, address.X, address.Y, RasterFormat.Png, MapRenderEngine.Version(service, style));
            var image = await render.Cache.TryGetAsync(key, cancellationToken);
            if (image is null)
            {
                var viewport = new RasterViewport(scheme.Bounds(coordinate), scheme.TileSize, scheme.TileSize, scheme.Crs);
                var sources = MapRenderEngine.Sources(stores, resolved.Store, layers, null);
                image = await render.Renderer.RenderAsync(
                    new MapRenderRequest(viewport, style, sources, null, RasterFormat.Png, 90, null, true, 1.0), cancellationToken);
                await render.Cache.SetAsync(key, image, cancellationToken);
            }

            GeoServicesResponses.WriteImageHeaders(context, image);
            return Results.Bytes(image.Content, image.MediaType);
        }
        catch (Exception exception)
        {
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

/// <summary>The MapServer tile address bound from the route via <c>[AsParameters]</c> (ADR-0040).</summary>
internal sealed class MapTileAddress
{
    public int Z { get; set; }

    public int X { get; set; }

    public int Y { get; set; }
}

/// <summary>The render seams one MapServer tile request needs, grouped so the handler stays within the parameter budget (ADR-0040).</summary>
internal sealed record MapTileRenderDependencies(IMapRenderer Renderer, ITileCache Cache, IReadOnlyList<ITileScheme> Schemes);
