using Spatial.Interop.Esri;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Providers;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The Map Service routes (spec §4, ADR-0048): root, all-layers, layer
/// metadata, query, identify and find. The MapServer is the GeoServices
/// projection of a <see cref="PublicationKind.Map"/> publication; its export
/// and tile routes live in <see cref="MapExportEndpoints"/>, which renders
/// through the SDK render contract.
/// </summary>
internal static class MapServerEndpoints
{
    internal static void MapMapServer(RouteGroupBuilder group, GeoServicesCatalog catalog, IPublicationRegistry registry)
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

        MapExportEndpoints.MapExportRoutes(group, catalog, registry);
    }

    private static async Task<IResult> MapServerRoot(
        GeoServicesCatalog catalog, IPublicationRegistry registry, string service, HttpContext context, IServiceProvider services, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            var resolved = await GeoServicesEndpoints.ResolveServiceAsync(catalog, registry, service, "MapServer", PublicationKind.Map, cancellationToken);
            var layers = await GeoServicesEndpoints.ListLayersAsync(services, resolved, cancellationToken);
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
            var resolved = await GeoServicesEndpoints.ResolveServiceAsync(catalog, registry, service, "MapServer", PublicationKind.Map, cancellationToken);
            var layers = await GeoServicesEndpoints.ListLayersAsync(services, resolved, cancellationToken);
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
            var resolved = await GeoServicesEndpoints.ResolveServiceAsync(catalog, registry, service, "MapServer", PublicationKind.Map, cancellationToken);
            var layers = await GeoServicesEndpoints.ListLayersAsync(services, resolved, cancellationToken);
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
            var resolved = await GeoServicesEndpoints.ResolveServiceAsync(catalog, registry, service, "MapServer", PublicationKind.Map, cancellationToken);
            var layers = await GeoServicesEndpoints.ListLayersAsync(services, resolved, cancellationToken);
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
            var resolved = await GeoServicesEndpoints.ResolveServiceAsync(catalog, registry, service, "MapServer", PublicationKind.Map, cancellationToken);
            var description = await GeoServicesEndpoints.DescribeAsync(services, resolved, layerId, cancellationToken);
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
            var resolved = await GeoServicesEndpoints.ResolveServiceAsync(catalog, registry, service, "MapServer", PublicationKind.Map, cancellationToken);
            var store = Store(services, resolved.Store);
            var layers = await GeoServicesEndpoints.ListLayersAsync(services, resolved, cancellationToken);
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
            var resolved = await GeoServicesEndpoints.ResolveServiceAsync(catalog, registry, service, "MapServer", PublicationKind.Map, cancellationToken);
            var store = Store(services, resolved.Store);
            var layers = await GeoServicesEndpoints.ListLayersAsync(services, resolved, cancellationToken);
            var infos = await MapService.ReadLayersAsync(store, Catalogue(services, resolved.Store), layers, cancellationToken);
            return await MapFindEngine.FindAsync(store, infos, parameters, transforms, cancellationToken);
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    internal static IFeatureStore Store(IServiceProvider services, string store) =>
        services.GetRequiredKeyedService<IFeatureStore>(store);

    internal static IDataCatalogue Catalogue(IServiceProvider services, string store) =>
        services.GetRequiredKeyedService<IDataCatalogue>(store);

    /// <summary>The Web-Mercator scheme when registered, else the first scheme (the tileInfo source).</summary>
    internal static ITileScheme? MapTileScheme(IServiceProvider services)
    {
        var schemes = services.GetServices<ITileScheme>().ToArray();
        return schemes.FirstOrDefault(scheme => MapService.SridOf(scheme.Crs) == 3857) ?? schemes.FirstOrDefault();
    }
}
