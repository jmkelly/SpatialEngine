using Microsoft.AspNetCore.Http;
using Spatial.Interop.Esri;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Providers;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// Mounts the GeoServices REST route group (ADR-0035, ADR-0041 §5): the
/// catalog, the Geometry Service and Feature Servers (query, feature resource
/// and gated editing). A Feature Server is resolved from the
/// <see cref="IMapRegistry"/> — a runtime publication created through
/// the neutral admin API is served immediately, with its persisted stable
/// layer ids — while configuration-declared services keep the whole-store
/// expansion (every dataset in the store, sorted, ids assigned once). Every
/// handler negotiates <c>f=json</c>, reads the merged request parameters and
/// maps failures to the Esri error envelope.
/// </summary>
public static partial class GeoServicesEndpoints
{
    /// <summary>Maps the facade at <see cref="GeoServicesOptions.Root"/>.</summary>
    public static void Map(IEndpointRouteBuilder app, GeoServicesOptions options, IMapRegistry registry)
    {
        var catalog = new GeoServicesCatalog(options);
        var group = app.MapGroup(catalog.Root);

        // Spec §2.0.1: a resource is requestable with GET or POST. ArcGIS
        // REST JS (and therefore the Maps SDK) POSTs resource reads, so the
        // catalog, Geometry Server and Feature Server roots accept both.
        group.MapMethods(string.Empty, ["GET", "POST"], (HttpContext context, CancellationToken cancellationToken) =>
            Catalog(catalog, registry, context, cancellationToken));
        GeometryServerEndpoints.MapGeometryServer(group);

        group.MapMethods("/{service}/FeatureServer", ["GET", "POST"], (string service, HttpContext context, IServiceProvider services, CancellationToken cancellationToken) =>
            FeatureServerRoot(catalog, registry, service, context, services, cancellationToken));
        group.MapMethods("/{service}/FeatureServer/{layerId:int}", ["GET", "POST"], (string service, int layerId, HttpContext context, IServiceProvider services, CancellationToken cancellationToken) =>
            FeatureLayer(catalog, registry, service, layerId, context, services, cancellationToken));
        // The Feature (object) resource (spec §9.1.2): ArcGIS REST JS
        // getFeature reads `<layerId>/<objectId>` directly.
        group.MapMethods("/{service}/FeatureServer/{layerId:int}/{objectId:long}", ["GET", "POST"], (
            string service, int layerId, long objectId, HttpContext context, IServiceProvider services,
            ICoordinateTransforms transforms, CancellationToken cancellationToken) =>
            FeatureResource(catalog, registry, service, layerId, objectId, context, services, transforms, cancellationToken));

        group.MapMethods("/{service}/FeatureServer/{layerId:int}/query", ["GET", "POST"], (
            HttpContext context,
            string service,
            int layerId,
            IServiceProvider services,
            IGeometryOperations operations,
            ICoordinateTransforms transforms,
            CancellationToken cancellationToken) =>
            FeatureQuery(
                new FeatureQueryContext(catalog, registry, context, service, layerId, services, operations, transforms),
                cancellationToken));

        // Editing (ADR-0037): POST-only, per spec §9.1.6–§9.1.9.
        group.MapPost("/{service}/FeatureServer/{layerId:int}/addFeatures", (
            HttpContext context, string service, int layerId, IServiceProvider services, CancellationToken cancellationToken) =>
            FeatureEdit(new FeatureEditContext(catalog, registry, context, service, layerId, services, EsriEditOperation.Add), cancellationToken));
        group.MapPost("/{service}/FeatureServer/{layerId:int}/updateFeatures", (
            HttpContext context, string service, int layerId, IServiceProvider services, CancellationToken cancellationToken) =>
            FeatureEdit(new FeatureEditContext(catalog, registry, context, service, layerId, services, EsriEditOperation.Update), cancellationToken));
        group.MapPost("/{service}/FeatureServer/{layerId:int}/deleteFeatures", (
            HttpContext context, string service, int layerId, IServiceProvider services, CancellationToken cancellationToken) =>
            FeatureEdit(new FeatureEditContext(catalog, registry, context, service, layerId, services, EsriEditOperation.Delete), cancellationToken));
        group.MapPost("/{service}/FeatureServer/{layerId:int}/applyEdits", (
            HttpContext context, string service, int layerId, IServiceProvider services, CancellationToken cancellationToken) =>
            FeatureEdit(new FeatureEditContext(catalog, registry, context, service, layerId, services, EsriEditOperation.Apply), cancellationToken));

        // The Map Service projection (spec §4, ADR-0048) and the Image
        // Service projection (spec §8, ADR-0051).
        MapServerEndpoints.MapMapServer(group, catalog, registry);
        ImageServerEndpoints.MapImageServer(group, catalog, registry, options);
    }

    private static async Task<IResult> Catalog(GeoServicesCatalog catalog, IMapRegistry registry, HttpContext context, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            var services = await BuildServicesAsync(catalog, registry, cancellationToken);
            return EsriJson.Value(new CatalogResponse(10.0, [], services.ToArray()));
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    /// <summary>
    /// The catalogue entries: a deterministic Geometry-first order built from
    /// the registry's publications plus the declared FeatureServer services.
    /// Declared services are also publications (seeded at composition), and a
    /// registry that is not populated (for example in unit tests) is tolerated.
    /// </summary>
    private static async Task<List<EsriServiceEntry>> BuildServicesAsync(
        GeoServicesCatalog catalog, IMapRegistry registry, CancellationToken cancellationToken)
    {
        var services = new List<EsriServiceEntry> { new(GeoServicesCatalog.GeometryServiceName, "GeometryServer") };
        foreach (var map in await registry.ListAsync(cancellationToken))
        {
            foreach (var service in map.Services)
            {
                if (ServerType(service) is { } type)
                {
                    services.Add(new EsriServiceEntry(map.Name, type));
                }
            }
        }

        foreach (var entry in catalog.Services)
        {
            AddDeclared(services, entry);
        }

        return services;
    }

    private static string? ServerType(MapService service) => service switch
    {
        MapService.Feature => "FeatureServer",
        MapService.Map => "MapServer",
        MapService.Image => "ImageServer",
        _ => null,
    };

    private static void AddDeclared(List<EsriServiceEntry> services, GeoServicesServiceEntry entry)
    {
        if (entry.Type == "FeatureServer"
            && !services.Any(service => string.Equals(service.Name, entry.Name, StringComparison.OrdinalIgnoreCase)))
        {
            services.Add(new EsriServiceEntry(entry.Name, entry.Type));
        }
    }

    private static async Task<IResult> FeatureServerRoot(
        GeoServicesCatalog catalog, IMapRegistry registry, string service, HttpContext context, IServiceProvider services, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            var resolved = await ResolveServiceAsync(catalog, registry, service, "FeatureServer", MapService.Feature, cancellationToken);
            var layers = await ListLayersAsync(services, resolved, cancellationToken);
            return EsriJson.Value(FeatureService.Root(layers, IsEditable(services, resolved.Store)));
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    private static async Task<IResult> FeatureLayer(
        GeoServicesCatalog catalog, IMapRegistry registry, string service, int layerId, HttpContext context, IServiceProvider services, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            var resolved = await ResolveServiceAsync(catalog, registry, service, "FeatureServer", MapService.Feature, cancellationToken);
            var description = await DescribeAsync(services, resolved, layerId, cancellationToken);
            var editable = IsEditable(services, resolved.Store) && EsriObjectIdScheme.For(description).SupportsEditing;
            return EsriJson.Value(FeatureService.Layer(layerId, description, editable));
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    private static async Task<IResult> FeatureResource(
        GeoServicesCatalog catalog,
        IMapRegistry registry,
        string service,
        int layerId,
        long objectId,
        HttpContext context,
        IServiceProvider services,
        ICoordinateTransforms transforms,
        CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            var resolved = await ResolveServiceAsync(catalog, registry, service, "FeatureServer", MapService.Feature, cancellationToken);
            var description = await DescribeAsync(services, resolved, layerId, cancellationToken);
            var query = EsriFeatureQuery.Parse(parameters, EsriLayerModel.LayerCoordinateReference(description.Srid));
            var store = services.GetRequiredKeyedService<IFeatureStore>(resolved.Store);
            return await FeatureService.FeatureAsync(description, store, objectId, query, transforms, cancellationToken);
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    private static async Task<IResult> FeatureQuery(FeatureQueryContext request, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(request.Context, cancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            var resolved = await ResolveServiceAsync(request.Catalog, request.Registry, request.Service, "FeatureServer", MapService.Feature, cancellationToken);
            var description = await DescribeAsync(request.Services, resolved, request.LayerId, cancellationToken);
            var query = EsriFeatureQuery.Parse(parameters, EsriLayerModel.LayerCoordinateReference(description.Srid));
            var store = request.Services.GetRequiredKeyedService<IFeatureStore>(resolved.Store);
            return await FeatureService.QueryAsync(description, store, query, request.Operations, request.Transforms, cancellationToken);
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    private static async Task<IResult> FeatureEdit(FeatureEditContext request, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(request.Context, cancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            EsriEditRequest.RejectUnsupported(parameters);
            var resolved = await ResolveServiceAsync(request.Catalog, request.Registry, request.Service, "FeatureServer", MapService.Feature, cancellationToken);
            var description = await DescribeAsync(request.Services, resolved, request.LayerId, cancellationToken);
            var store = request.Services.GetRequiredKeyedService<IFeatureStore>(resolved.Store);
            var editStore = EditStore(request.Services, resolved.Store)
                ?? throw EsriInteropException.Invalid(
                    $"Service '{request.Service}' is read-only; it exposes no feature-editing capability.");

            var edits = request.Operation switch
            {
                EsriEditOperation.Add => EsriEditRequest.ParseAdd(parameters),
                EsriEditOperation.Update => EsriEditRequest.ParseUpdate(parameters),
                EsriEditOperation.Delete => EsriEditRequest.ParseDelete(parameters),
                _ => EsriEditRequest.ParseApply(parameters),
            };
            return await FeatureService.EditsAsync(
                request.Operation,
                description,
                store,
                editStore,
                edits,
                EsriLayerModel.LayerCoordinateReference(description.Srid),
                cancellationToken);
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    private static bool IsEditable(IServiceProvider services, string store) =>
        EditStore(services, store) is not null;

    /// <summary>Resolves the service's keyed editing capability, or null when the store is read-only (ADR-0037).</summary>
    private static IFeatureEditStore? EditStore(IServiceProvider services, string store) =>
        services.GetKeyedService<IFeatureEditStore>(store);

    /// <summary>
    /// Resolves one GeoServices server (Feature, Map or Image): a
    /// config-declared service exposes its whole store (sorted, index-assigned
    /// layers), a runtime map exposes the persisted explicit layers that feed
    /// the requested service (ADR-0053). A map that does not expose the
    /// service, or an unknown name, is not-found.
    /// </summary>
    internal static async Task<ResolvedService> ResolveServiceAsync(
        GeoServicesCatalog catalog,
        IMapRegistry registry,
        string service,
        string serverType,
        MapService mapService,
        CancellationToken cancellationToken)
    {
        if (catalog.TryGet(service, out var entry) && entry.Type == serverType)
        {
            return new ResolvedService(entry.Store, null);
        }

        Map map;
        try
        {
            map = await registry.GetAsync(service, cancellationToken);
        }
        catch (SpatialException exception) when (exception.Code == SpatialException.NotFound)
        {
            throw new EsriInteropException(EsriErrorCodes.NotFound, $"Service '{service}' was not found.");
        }

        if (!map.Exposes(mapService))
        {
            throw new EsriInteropException(EsriErrorCodes.NotFound, $"Service '{service}' was not found.");
        }

        var layers = map.Layers.Where(layer => Feeds(mapService, layer.Kind)).ToArray();
        return new ResolvedService(map.Store, layers, map.Description, map.Copyright);
    }

    /// <summary>Whether a layer of <paramref name="kind"/> feeds <paramref name="service"/>.</summary>
    internal static bool Feeds(MapService service, MapLayerKind kind) =>
        service == MapService.Image ? kind == MapLayerKind.Image : kind == MapLayerKind.Feature;

    /// <summary>Lists the published layers: explicit for a runtime publication, whole-store (sorted) for a declared service.</summary>
    internal static async Task<IReadOnlyList<PublishedLayer>> ListLayersAsync(
        IServiceProvider services, ResolvedService resolved, CancellationToken cancellationToken)
    {
        if (resolved.Layers is { } layers)
        {
            return layers
                .OrderBy(layer => layer.LayerId)
                .Select(layer => new PublishedLayer(layer.LayerId, layer.Dataset, layer.Name ?? Table(layer.Dataset), layer.Style))
                .ToArray();
        }

        var catalogue = services.GetRequiredKeyedService<IDataCatalogue>(resolved.Store);
        var datasets = (await catalogue.ListAsync(null, cancellationToken)).OrderBy(dataset => dataset.Id, StringComparer.Ordinal).ToArray();
        return datasets.Select((dataset, index) => new PublishedLayer(index, dataset.Id, dataset.Table)).ToArray();
    }

    internal static async Task<DatasetDescription> DescribeAsync(
        IServiceProvider services, ResolvedService resolved, int layerId, CancellationToken cancellationToken)
    {
        var layers = await ListLayersAsync(services, resolved, cancellationToken);
        var layer = layers.FirstOrDefault(candidate => candidate.Id == layerId)
            ?? throw new EsriInteropException(EsriErrorCodes.NotFound, $"Layer {layerId} does not exist in the service.");
        var catalogue = services.GetRequiredKeyedService<IDataCatalogue>(resolved.Store);
        return await catalogue.DescribeAsync(layer.Dataset, cancellationToken);
    }

    private static string Table(string dataset)
    {
        var dot = dataset.IndexOf('.');
        return dot < 0 ? dataset : dataset[(dot + 1)..];
    }
}

/// <summary>One layer resolved for serving (id via the publication or the whole-store order).</summary>
internal sealed record PublishedLayer(int Id, string Dataset, string Name, string? Style = null);

/// <summary>A resolved GeoServices server: its store and its explicit layers (null means whole-store).</summary>
internal sealed record ResolvedService(
    string Store,
    IReadOnlyList<MapLayer>? Layers,
    string? Description = null,
    string? Copyright = null);

/// <summary>The resolved services of one Feature Service query request.</summary>
internal sealed record FeatureQueryContext(
    GeoServicesCatalog Catalog,
    IMapRegistry Registry,
    HttpContext Context,
    string Service,
    int LayerId,
    IServiceProvider Services,
    IGeometryOperations Operations,
    ICoordinateTransforms Transforms);

/// <summary>The resolved services of one Feature Service editing request.</summary>
internal sealed record FeatureEditContext(
    GeoServicesCatalog Catalog,
    IMapRegistry Registry,
    HttpContext Context,
    string Service,
    int LayerId,
    IServiceProvider Services,
    EsriEditOperation Operation);

/// <summary>The GeoServices catalog resource (spec §3).</summary>
internal sealed record CatalogResponse(double CurrentVersion, IReadOnlyList<string> Folders, IReadOnlyList<EsriServiceEntry> Services);

/// <summary>One catalog service entry.</summary>
internal sealed record EsriServiceEntry(string Name, string Type);
