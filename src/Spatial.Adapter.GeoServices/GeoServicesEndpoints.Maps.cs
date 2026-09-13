using System.Globalization;
using Spatial.Core.Geometry;
using Spatial.Interop.Esri;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Providers;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The Map Service routes (spec §4, ADR-0048): root, all-layers, layer
/// metadata, query, identify, find, export and tiles. The MapServer is the
/// GeoServices projection of a <see cref="PublicationKind.Map"/> publication;
/// export and tiles render through the SDK render contract, never a host type.
/// </summary>
public static partial class GeoServicesEndpoints
{
    private static void MapMapServer(RouteGroupBuilder group, GeoServicesCatalog catalog, IPublicationRegistry registry)
    {
        group.MapMethods("/{service}/MapServer", ["GET", "POST"], (string service, HttpContext context, IServiceProvider services, CancellationToken cancellationToken) =>
            MapServerRoot(catalog, registry, service, context, services, cancellationToken));
        group.MapMethods("/{service}/MapServer/layers", ["GET", "POST"], (string service, HttpContext context, IServiceProvider services, CancellationToken cancellationToken) =>
            MapAllLayers(catalog, registry, service, context, services, cancellationToken));
        group.MapMethods("/{service}/MapServer/{layerId:int}", ["GET", "POST"], (string service, int layerId, HttpContext context, IServiceProvider services, CancellationToken cancellationToken) =>
            MapLayer(catalog, registry, service, layerId, context, services, cancellationToken));
        group.MapMethods("/{service}/MapServer/{layerId:int}/images/{imageId}", ["GET", "POST"], (
            string service, int layerId, string imageId, HttpContext context, IServiceProvider services, CancellationToken cancellationToken) =>
            MapImage(catalog, registry, service, layerId, imageId, context, services, cancellationToken));
        group.MapMethods("/{service}/MapServer/{layerId:int}/query", ["GET", "POST"], (
            string service, int layerId, HttpContext context, IServiceProvider services,
            IGeometryOperations operations, ICoordinateTransforms transforms, CancellationToken cancellationToken) =>
            MapQuery(catalog, registry, service, layerId, context, services, operations, transforms, cancellationToken));
        group.MapMethods("/{service}/MapServer/identify", ["GET", "POST"], (
            string service, HttpContext context, IServiceProvider services,
            IGeometryOperations operations, ICoordinateTransforms transforms, CancellationToken cancellationToken) =>
            MapIdentify(catalog, registry, service, context, services, operations, transforms, cancellationToken));
        group.MapMethods("/{service}/MapServer/find", ["GET", "POST"], (
            string service, HttpContext context, IServiceProvider services, ICoordinateTransforms transforms, CancellationToken cancellationToken) =>
            MapFind(catalog, registry, service, context, services, transforms, cancellationToken));
        group.MapMethods("/{service}/MapServer/export", ["GET", "POST"], (
            string service, HttpContext context, IServiceProvider services, ICoordinateTransforms transforms, CancellationToken cancellationToken) =>
            MapExport(catalog, registry, service, context, services, transforms, cancellationToken));
        group.MapMethods("/{service}/MapServer/tile/{z:int}/{y:int}/{x:int}", ["GET", "POST"], (
            string service, int z, int y, int x, HttpContext context, IServiceProvider services, CancellationToken cancellationToken) =>
            MapTile(catalog, registry, service, z, y, x, context, services, cancellationToken));
    }

    private static async Task<IResult> MapServerRoot(
        GeoServicesCatalog catalog, IPublicationRegistry registry, string service, HttpContext context, IServiceProvider services, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            var resolved = await ResolveServiceAsync(catalog, registry, service, "MapServer", PublicationKind.Map, cancellationToken);
            var layers = await ListLayersAsync(services, resolved, cancellationToken);
            var infos = await MapService.ReadLayersAsync(Store(services, resolved.Store), Catalogue(services, resolved.Store), layers, cancellationToken);
            return EsriJson.Value(MapService.Root(infos, MapTileScheme(services), resolved.Description, resolved.Copyright));
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    private static async Task<IResult> MapAllLayers(
        GeoServicesCatalog catalog, IPublicationRegistry registry, string service, HttpContext context, IServiceProvider services, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            var resolved = await ResolveServiceAsync(catalog, registry, service, "MapServer", PublicationKind.Map, cancellationToken);
            var layers = await ListLayersAsync(services, resolved, cancellationToken);
            return EsriJson.Value(MapService.AllLayers(layers));
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    private static async Task<IResult> MapLayer(
        GeoServicesCatalog catalog, IPublicationRegistry registry, string service, int layerId, HttpContext context, IServiceProvider services, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            var resolved = await ResolveServiceAsync(catalog, registry, service, "MapServer", PublicationKind.Map, cancellationToken);
            var layers = await ListLayersAsync(services, resolved, cancellationToken);
            var layer = layers.FirstOrDefault(candidate => candidate.Id == layerId)
                ?? throw new EsriInteropException(EsriErrorCodes.NotFound, $"Layer {layerId} does not exist in service '{service}'.");
            var info = await MapService.ReadLayerAsync(Store(services, resolved.Store), Catalogue(services, resolved.Store), layer, cancellationToken);
            return EsriJson.Value(MapService.Layer(info));
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    /// <summary>
    /// The MapServer image resource (spec §4.7). It exists only for picture
    /// marker/fill symbols, whose <c>url</c> is the <c>imageId</c>. The engine's
    /// MapLibre dialect has no picture symbols and stores no symbol images, so
    /// the resource is genuinely blocked and reports a typed <c>not.found</c>
    /// rather than serving a stub (ADR-0050).
    /// </summary>
    private static async Task<IResult> MapImage(
        GeoServicesCatalog catalog, IPublicationRegistry registry, string service, int layerId, string imageId,
        HttpContext context, IServiceProvider services, CancellationToken cancellationToken)
    {
        try
        {
            var resolved = await ResolveServiceAsync(catalog, registry, service, "MapServer", PublicationKind.Map, cancellationToken);
            var layers = await ListLayersAsync(services, resolved, cancellationToken);
            _ = layers.FirstOrDefault(candidate => candidate.Id == layerId)
                ?? throw new EsriInteropException(EsriErrorCodes.NotFound, $"Layer {layerId} does not exist in service '{service}'.");
            throw new EsriInteropException(
                EsriErrorCodes.NotFound,
                $"Image '{imageId}' is not available: MapServer image resources exist only for picture marker/fill symbols (spec §4.7), and the engine's style dialect has no picture symbols.");
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    private static async Task<IResult> MapQuery(
        GeoServicesCatalog catalog, IPublicationRegistry registry, string service, int layerId, HttpContext context, IServiceProvider services,
        IGeometryOperations operations, ICoordinateTransforms transforms, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            var resolved = await ResolveServiceAsync(catalog, registry, service, "MapServer", PublicationKind.Map, cancellationToken);
            var description = await DescribeAsync(services, resolved, layerId, cancellationToken);
            var query = EsriFeatureQuery.Parse(parameters, EsriLayerModel.LayerCoordinateReference(description.Srid));
            var store = Store(services, resolved.Store);
            return await FeatureService.QueryAsync(description, store, query, operations, transforms, cancellationToken);
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    private static async Task<IResult> MapIdentify(
        GeoServicesCatalog catalog, IPublicationRegistry registry, string service, HttpContext context, IServiceProvider services,
        IGeometryOperations operations, ICoordinateTransforms transforms, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            var resolved = await ResolveServiceAsync(catalog, registry, service, "MapServer", PublicationKind.Map, cancellationToken);
            var store = Store(services, resolved.Store);
            var layers = await ListLayersAsync(services, resolved, cancellationToken);
            var infos = await MapService.ReadLayersAsync(store, Catalogue(services, resolved.Store), layers, cancellationToken);
            return await MapIdentifyEngine.IdentifyAsync(
                store, infos, parameters, MapService.MapSrid(infos), operations, transforms, cancellationToken);
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    private static async Task<IResult> MapFind(
        GeoServicesCatalog catalog, IPublicationRegistry registry, string service, HttpContext context, IServiceProvider services,
        ICoordinateTransforms transforms, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            var resolved = await ResolveServiceAsync(catalog, registry, service, "MapServer", PublicationKind.Map, cancellationToken);
            var store = Store(services, resolved.Store);
            var layers = await ListLayersAsync(services, resolved, cancellationToken);
            var infos = await MapService.ReadLayersAsync(store, Catalogue(services, resolved.Store), layers, cancellationToken);
            return await MapFindEngine.FindAsync(store, infos, parameters, transforms, cancellationToken);
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    private static async Task<IResult> MapExport(
        GeoServicesCatalog catalog, IPublicationRegistry registry, string service, HttpContext context, IServiceProvider services,
        ICoordinateTransforms transforms, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            var resolved = await ResolveServiceAsync(catalog, registry, service, "MapServer", PublicationKind.Map, cancellationToken);
            var selected = MapLayerSelection.Select(await ListLayersAsync(services, resolved, cancellationToken), parameters.Get("layers"));
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
                WriteImageHeaders(context, image);
                return Results.Bytes(image.Content, image.MediaType);
            }

            var dpi = MapRenderEngine.Dpi(parameters.Get("dpi"));
            return EsriJson.Value(new EsriMapExportResponse(
                ExportHref(context),
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
        GeoServicesCatalog catalog, IPublicationRegistry registry, string service, int z, int y, int x,
        HttpContext context, IServiceProvider services, CancellationToken cancellationToken)
    {
        try
        {
            var resolved = await ResolveServiceAsync(catalog, registry, service, "MapServer", PublicationKind.Map, cancellationToken);
            var layers = await ListLayersAsync(services, resolved, cancellationToken);
            var scheme = MapTileScheme(services)
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

            WriteImageHeaders(context, image);
            return Results.Bytes(image.Content, image.MediaType);
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    private static IFeatureStore Store(IServiceProvider services, string store) =>
        services.GetRequiredKeyedService<IFeatureStore>(store);

    private static IDataCatalogue Catalogue(IServiceProvider services, string store) =>
        services.GetRequiredKeyedService<IDataCatalogue>(store);

    private static IMapRenderer Renderer(IServiceProvider services) =>
        services.GetService<IMapRenderer>()
        ?? throw new EsriInteropException(EsriErrorCodes.ServiceUnavailable, "Rendering is not configured on this host.");

    /// <summary>The Web-Mercator scheme when registered, else the first scheme (the tileInfo source).</summary>
    private static ITileScheme? MapTileScheme(IServiceProvider services)
    {
        var schemes = services.GetServices<ITileScheme>().ToArray();
        return schemes.FirstOrDefault(scheme => MapService.SridOf(scheme.Crs) == 3857) ?? schemes.FirstOrDefault();
    }

    private static async Task<CoordinateReference?> MapCrsAsync(
        IServiceProvider services, ResolvedService resolved, IReadOnlyList<PublishedLayer> layers, CancellationToken cancellationToken)
    {
        if (layers.Count == 0)
        {
            return null;
        }

        var description = await Catalogue(services, resolved.Store).DescribeAsync(layers[0].Dataset, cancellationToken);
        return EsriLayerModel.LayerCoordinateReference(description.Srid);
    }

    private static void WriteImageHeaders(HttpContext context, RasterImage image)
    {
        context.Response.Headers["X-Raster-Width"] = image.Width.ToString(CultureInfo.InvariantCulture);
        context.Response.Headers["X-Raster-Height"] = image.Height.ToString(CultureInfo.InvariantCulture);
        context.Response.Headers["X-Raster-Format"] = image.Format.ToString();
    }

    private static EsriExtent ExportExtent(Envelope bounds, CoordinateReference? crs) =>
        new(bounds.MinX, bounds.MinY, bounds.MaxX, bounds.MaxY, EsriLayerModel.SpatialReference(MapService.SridOf(crs?.ToString() ?? string.Empty)));

    private static string ExportHref(HttpContext context)
    {
        var query = context.Request.Query.ToDictionary(pair => pair.Key, pair => (string?)pair.Value.ToString());
        query["f"] = "image";
        return $"{context.Request.Scheme}://{context.Request.Host}{context.Request.Path}{QueryString.Create(query)}";
    }
}
