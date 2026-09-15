using Spatial.Esri.Codec;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Providers;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The Map Service routes (spec §4, ADR-0048/ADR-0055): root, all-layers, layer
/// metadata, query, identify, find, legend, queryDomains, queryLegends and
/// per-layer generateRenderer. The MapServer is the GeoServices
/// projection of a <see cref="MapServiceKind.MapServer"/> publication; its export
/// and tile routes live in <see cref="MapExportEndpoints"/>, which renders
/// through the SDK render contract.
/// </summary>
internal static class MapServerEndpoints
{
    internal static void MapMapServer(RouteGroupBuilder group, GeoServicesCatalog catalog, IMapRegistry registry)
    {
        group.MapMethods("/{service}/MapServer", ["GET", "POST"], (string service, HttpContext context, IStoreRegistry stores, IEnumerable<ITileScheme> schemes, CancellationToken cancellationToken) =>
            MapServerRoot(catalog, registry, service, context, stores, schemes, cancellationToken));
        group.MapMethods("/{service}/MapServer/layers", ["GET", "POST"], (string service, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            MapAllLayers(catalog, registry, service, context, stores, cancellationToken));
        group.MapMethods("/{service}/MapServer/{layerId:int}", ["GET", "POST"], (string service, int layerId, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            MapLayer(catalog, registry, service, layerId, context, stores, cancellationToken));
        group.MapMethods("/{service}/MapServer/legend", ["GET", "POST"], (string service, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            MapLegend(catalog, registry, service, context, stores, cancellationToken));
        group.MapMethods("/{service}/MapServer/queryDomains", ["GET", "POST"], (string service, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            MapQueryDomains(catalog, registry, service, context, stores, cancellationToken));
        group.MapMethods("/{service}/MapServer/queryLegends", ["GET", "POST"], (string service, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            MapQueryLegends(catalog, registry, service, context, stores, cancellationToken));
        group.MapMethods("/{service}/MapServer/{layerId:int}/generateRenderer", ["GET", "POST"], (
            string service, int layerId, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            MapGenerateRenderer(catalog, registry, service, layerId, context, stores, cancellationToken));
        group.MapMethods("/{service}/MapServer/{layerId:int}/images/{imageId}", ["GET", "POST"], (
            string service, int layerId, string imageId, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            MapImage(catalog, registry, service, layerId, imageId, context, stores, cancellationToken));
        group.MapMethods("/{service}/MapServer/{layerId:int}/query", ["GET", "POST"], (
            string service, int layerId, HttpContext context, IStoreRegistry stores,
            IGeometryOperations operations, ICoordinateTransforms transforms, CancellationToken cancellationToken) =>
            MapQuery(catalog, registry, service, layerId, context, stores, operations, transforms, cancellationToken));
        group.MapMethods("/{service}/MapServer/identify", ["GET", "POST"], (
            string service, HttpContext context, IStoreRegistry stores,
            IGeometryOperations operations, ICoordinateTransforms transforms, CancellationToken cancellationToken) =>
            MapIdentify(catalog, registry, service, context, stores, operations, transforms, cancellationToken));
        group.MapMethods("/{service}/MapServer/find", ["GET", "POST"], (
            string service, HttpContext context, IStoreRegistry stores, ICoordinateTransforms transforms, CancellationToken cancellationToken) =>
            MapFind(catalog, registry, service, context, stores, transforms, cancellationToken));
        MapOfflineEndpoints(catalog, registry, group);

        MapExportEndpoints.MapExportRoutes(group, catalog, registry);
    }

    /// <summary>
    /// The offline/async reject surface (ADR-0060): <c>exportTiles</c> +
    /// <c>estimateExportTileSize</c>, WMTS (base plus the capabilities/tile
    /// remainder), KML (<c>generateKml</c> plus the <c>kml</c> image
    /// remainder) and async <c>jobs</c> (collection plus one job, its results
    /// and its inputs). Named non-goals rejected by name; T-048 builds on
    /// this scope instead of re-deciding it.
    /// </summary>
    private static void MapOfflineEndpoints(GeoServicesCatalog catalog, IMapRegistry registry, RouteGroupBuilder group)
    {
        group.MapMethods("/{service}/MapServer/exportTiles", ["GET", "POST"], (
            string service, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            MapOfflineRejects.MapExportTiles(catalog, registry, service, cancellationToken));
        group.MapMethods("/{service}/MapServer/estimateExportTileSize", ["GET", "POST"], (
            string service, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            MapOfflineRejects.MapEstimateExportTileSize(catalog, registry, service, cancellationToken));
        group.MapMethods("/{service}/MapServer/WMTS", ["GET", "POST"], (
            string service, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            MapOfflineRejects.Wmts(catalog, registry, service, cancellationToken));
        group.MapMethods("/{service}/MapServer/WMTS/{*rest}", ["GET", "POST"], (
            string service, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            MapOfflineRejects.Wmts(catalog, registry, service, cancellationToken));
        group.MapMethods("/{service}/MapServer/generateKml", ["GET", "POST"], (
            string service, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            MapOfflineRejects.GenerateKml(catalog, registry, service, cancellationToken));
        group.MapMethods("/{service}/MapServer/kml/{*rest}", ["GET", "POST"], (
            string service, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            MapOfflineRejects.KmlImage(catalog, registry, service, cancellationToken));
        group.MapMethods("/{service}/MapServer/jobs", ["GET", "POST"], (
            string service, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            MapOfflineRejects.Jobs(catalog, registry, service, cancellationToken));
        group.MapMethods("/{service}/MapServer/jobs/{*rest}", ["GET", "POST"], (
            string service, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            MapOfflineRejects.Jobs(catalog, registry, service, cancellationToken));
    }

    private static async Task<IResult> MapServerRoot(
        GeoServicesCatalog catalog, IMapRegistry registry, string service, HttpContext context, IStoreRegistry stores,
        IEnumerable<ITileScheme> schemes, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            var resolved = await GeoServicesResolution.ResolveServiceAsync(catalog, registry, service, "MapServer", MapServiceKind.MapServer, cancellationToken);
            var layers = await GeoServicesResolution.ListLayersAsync(stores, resolved, cancellationToken);
            var infos = await MapServerResources.ReadLayersAsync(Store(stores, resolved.Store), Catalogue(stores, resolved.Store), layers, cancellationToken);
            return EsriJson.Value(MapServerResources.Root(service, infos, MapTileScheme(schemes), resolved.Description, resolved.Copyright));
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    private static async Task<IResult> MapAllLayers(
        GeoServicesCatalog catalog, IMapRegistry registry, string service, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            var resolved = await GeoServicesResolution.ResolveServiceAsync(catalog, registry, service, "MapServer", MapServiceKind.MapServer, cancellationToken);
            var layers = await GeoServicesResolution.ListLayersAsync(stores, resolved, cancellationToken);
            return EsriJson.Value(MapServerResources.AllLayers(layers));
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    private static async Task<IResult> MapLayer(
        GeoServicesCatalog catalog, IMapRegistry registry, string service, int layerId, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            var resolved = await GeoServicesResolution.ResolveServiceAsync(catalog, registry, service, "MapServer", MapServiceKind.MapServer, cancellationToken);
            var layers = await GeoServicesResolution.ListLayersAsync(stores, resolved, cancellationToken);
            var layer = layers.FirstOrDefault(candidate => candidate.Id == layerId)
                ?? throw GeoServicesErrors.NotFound($"Layer {layerId} does not exist in service '{service}'.");
            var info = await MapServerResources.ReadLayerAsync(Store(stores, resolved.Store), Catalogue(stores, resolved.Store), layer, cancellationToken);
            return EsriJson.Value(MapServerResources.Layer(info));
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    /// <summary>
    /// The MapServer <c>legend</c> resource (S4 legend-map-service/, ADR-0055):
    /// one swatch legend per layer, projected from the persisted style so it
    /// always agrees with the layer metadata <c>drawingInfo</c>.
    /// </summary>
    private static async Task<IResult> MapLegend(
        GeoServicesCatalog catalog, IMapRegistry registry, string service, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            var resolved = await GeoServicesResolution.ResolveServiceAsync(catalog, registry, service, "MapServer", MapServiceKind.MapServer, cancellationToken);
            var layers = await GeoServicesResolution.ListLayersAsync(stores, resolved, cancellationToken);
            var infos = await MapServerResources.ReadLayersAsync(Store(stores, resolved.Store), Catalogue(stores, resolved.Store), layers, cancellationToken);
            return EsriJson.Value(Adapter.GeoServices.MapLegend.Legend(infos));
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    /// <summary>
    /// The MapServer <c>queryDomains</c> operation (S4, ADR-0055): the
    /// projected domains of the selected layers (all layers when
    /// <c>layers</c> is absent). Unknown layer ids are typed
    /// <c>not.found</c>.
    /// </summary>
    private static async Task<IResult> MapQueryDomains(
        GeoServicesCatalog catalog, IMapRegistry registry, string service, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            var resolved = await GeoServicesResolution.ResolveServiceAsync(catalog, registry, service, "MapServer", MapServiceKind.MapServer, cancellationToken);
            var layers = await GeoServicesResolution.ListLayersAsync(stores, resolved, cancellationToken);
            var infos = await MapServerResources.ReadLayersAsync(Store(stores, resolved.Store), Catalogue(stores, resolved.Store), layers, cancellationToken);
            return EsriJson.Value(Adapter.GeoServices.MapLegend.QueryDomains(Select(infos, parameters.Get("layers"), service)));
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    /// <summary>
    /// The MapServer <c>queryLegends</c> operation (S4, ADR-0055): the legend
    /// of the selected layers (all layers when <c>layers</c> is absent).
    /// Unknown layer ids are typed <c>not.found</c>.
    /// </summary>
    private static async Task<IResult> MapQueryLegends(
        GeoServicesCatalog catalog, IMapRegistry registry, string service, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            var resolved = await GeoServicesResolution.ResolveServiceAsync(catalog, registry, service, "MapServer", MapServiceKind.MapServer, cancellationToken);
            var layers = await GeoServicesResolution.ListLayersAsync(stores, resolved, cancellationToken);
            var infos = await MapServerResources.ReadLayersAsync(Store(stores, resolved.Store), Catalogue(stores, resolved.Store), layers, cancellationToken);
            return EsriJson.Value(Adapter.GeoServices.MapLegend.QueryLegends(Select(infos, parameters.Get("layers"), service)));
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    /// <summary>
    /// The per-layer <c>generateRenderer</c> operation (S4, ADR-0055):
    /// server-side classification over the layer's data. This is the single
    /// map-service implementation; the feature write-model track reuses it.
    /// </summary>
    private static async Task<IResult> MapGenerateRenderer(
        GeoServicesCatalog catalog, IMapRegistry registry, string service, int layerId, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            var resolved = await GeoServicesResolution.ResolveServiceAsync(catalog, registry, service, "MapServer", MapServiceKind.MapServer, cancellationToken);
            var layers = await GeoServicesResolution.ListLayersAsync(stores, resolved, cancellationToken);
            _ = layers.FirstOrDefault(candidate => candidate.Id == layerId)
                ?? throw GeoServicesErrors.NotFound($"Layer {layerId} does not exist in service '{service}'.");
            var description = await GeoServicesResolution.DescribeAsync(stores, resolved, layerId, cancellationToken);
            var renderer = await Adapter.GeoServices.MapGenerateRenderer.GenerateAsync(
                Store(stores, resolved.Store), description, parameters.Get("classificationDef"), parameters.Get("where"), cancellationToken);
            return EsriJson.Value(new EsriGenerateRendererResponse(renderer));
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    /// <summary>
    /// Selects legend/domain layers by the <c>layers</c> parameter (a bare id
    /// list, or the shared show/hide/all grammar); unknown ids are typed
    /// <c>not.found</c> rather than silently dropped.
    /// </summary>
    private static IReadOnlyList<MapLayerInfo> Select(IReadOnlyList<MapLayerInfo> infos, string? selection, string service)
    {
        var selected = MapLayerSelection.Select(infos, selection);
        if (selection is not null && selected.Count != infos.Count)
        {
            ThrowIfUnknownLayer(infos, selection, service);
        }

        return selected;
    }

    private static void ThrowIfUnknownLayer(IReadOnlyList<MapLayerInfo> infos, string selection, string service)
    {
        var known = infos.Select(info => info.Layer.Id).ToHashSet();
        var unknown = RequestedIds(selection).Except(known).ToList();
        if (unknown.Count != 0)
        {
            throw GeoServicesErrors.NotFound($"Layer {unknown[0]} does not exist in service '{service}'.");
        }
    }

    internal static IReadOnlyList<int> RequestedIds(string selection)
    {
        var text = StripShowPrefix(selection.Trim());
        if (text is null)
        {
            return [];
        }

        if (IsSelectAllKeyword(text))
        {
            return [];
        }

        return TryParseIdList(text);
    }

    private static string? StripShowPrefix(string text)
    {
        if (text.StartsWith("show:", StringComparison.OrdinalIgnoreCase))
        {
            return text[5..];
        }

        return text.StartsWith("hide:", StringComparison.OrdinalIgnoreCase) ? null : text;
    }

    private static bool IsSelectAllKeyword(string text) =>
        string.IsNullOrWhiteSpace(text)
            || text.Equals("all", StringComparison.OrdinalIgnoreCase)
            || text.Equals("visible", StringComparison.OrdinalIgnoreCase)
            || text.Equals("top", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<int> TryParseIdList(string text)
    {
        try
        {
            return [.. EsriValueParser.ParseInt64s(text, "layers").Select(value => (int)value)];
        }
        catch (EsriInteropException)
        {
            return [];
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
        GeoServicesCatalog catalog, IMapRegistry registry, string service, int layerId, string imageId,
        HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken)
    {
        try
        {
            var resolved = await GeoServicesResolution.ResolveServiceAsync(catalog, registry, service, "MapServer", MapServiceKind.MapServer, cancellationToken);
            var layers = await GeoServicesResolution.ListLayersAsync(stores, resolved, cancellationToken);
            _ = layers.FirstOrDefault(candidate => candidate.Id == layerId)
                ?? throw GeoServicesErrors.NotFound($"Layer {layerId} does not exist in service '{service}'.");
            throw GeoServicesErrors.NotFound(
                $"Image '{imageId}' is not available: MapServer image resources exist only for picture marker/fill symbols (spec §4.7), and the engine's style dialect has no picture symbols.");
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    private static async Task<IResult> MapQuery(
        GeoServicesCatalog catalog, IMapRegistry registry, string service, int layerId, HttpContext context, IStoreRegistry stores,
        IGeometryOperations operations, ICoordinateTransforms transforms, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            var resolved = await GeoServicesResolution.ResolveServiceAsync(catalog, registry, service, "MapServer", MapServiceKind.MapServer, cancellationToken);
            var description = await GeoServicesResolution.DescribeAsync(stores, resolved, layerId, cancellationToken);
            var query = EsriFeatureQuery.Parse(parameters, EsriLayerModel.LayerCoordinateReference(description.Srid));
            var store = Store(stores, resolved.Store);
            return await FeatureService.QueryAsync(description, store, query, operations, transforms, cancellationToken);
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    private static async Task<IResult> MapIdentify(
        GeoServicesCatalog catalog, IMapRegistry registry, string service, HttpContext context, IStoreRegistry stores,
        IGeometryOperations operations, ICoordinateTransforms transforms, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            var resolved = await GeoServicesResolution.ResolveServiceAsync(catalog, registry, service, "MapServer", MapServiceKind.MapServer, cancellationToken);
            var store = Store(stores, resolved.Store);
            var layers = await GeoServicesResolution.ListLayersAsync(stores, resolved, cancellationToken);
            var infos = await MapServerResources.ReadLayersAsync(store, Catalogue(stores, resolved.Store), layers, cancellationToken);
            return await MapIdentifyEngine.IdentifyAsync(
                store, infos, parameters, MapServerResources.MapSrid(infos), operations, transforms, cancellationToken);
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    private static async Task<IResult> MapFind(
        GeoServicesCatalog catalog, IMapRegistry registry, string service, HttpContext context, IStoreRegistry stores,
        ICoordinateTransforms transforms, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            var resolved = await GeoServicesResolution.ResolveServiceAsync(catalog, registry, service, "MapServer", MapServiceKind.MapServer, cancellationToken);
            var store = Store(stores, resolved.Store);
            var layers = await GeoServicesResolution.ListLayersAsync(stores, resolved, cancellationToken);
            var infos = await MapServerResources.ReadLayersAsync(store, Catalogue(stores, resolved.Store), layers, cancellationToken);
            return await MapFindEngine.FindAsync(store, infos, parameters, transforms, cancellationToken);
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    internal static IFeatureStore Store(IStoreRegistry stores, string store) =>
        stores.Features(store);

    internal static IDataCatalogue Catalogue(IStoreRegistry stores, string store) =>
        stores.Catalogue(store);

    /// <summary>The Web-Mercator scheme when registered, else the first scheme (the tileInfo source).</summary>
    internal static ITileScheme? MapTileScheme(IEnumerable<ITileScheme> schemes)
    {
        var registered = schemes.ToArray();
        return registered.FirstOrDefault(scheme => MapServerResources.SridOf(scheme.Crs) == 3857) ?? registered.FirstOrDefault();
    }
}
