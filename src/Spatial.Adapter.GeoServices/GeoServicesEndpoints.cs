using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
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

        group.MapMethods("/{service}/FeatureServer", ["GET", "POST"], (string service, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            FeatureServerRoot(catalog, registry, service, context, stores, cancellationToken));
        group.MapMethods("/{service}/FeatureServer/{layerId:int}", ["GET", "POST"], (string service, int layerId, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            FeatureLayer(new FeatureLayerContext(catalog, registry, service, layerId, context, stores), cancellationToken));
        // The Feature (object) resource (spec §9.1.2): ArcGIS REST JS
        // getFeature reads `<layerId>/<objectId>` directly.
        group.MapMethods("/{service}/FeatureServer/{layerId:int}/{objectId:long}", ["GET", "POST"], (
            string service, int layerId, long objectId, HttpContext context, IStoreRegistry stores,
            ICoordinateTransforms transforms, CancellationToken cancellationToken) =>
            FeatureResource(new FeatureResourceContext(catalog, registry, service, layerId, objectId, context, stores, transforms), cancellationToken));

        group.MapMethods("/{service}/FeatureServer/{layerId:int}/query", ["GET", "POST"], (
            HttpContext context,
            string service,
            int layerId,
            IStoreRegistry stores,
            IGeometryOperations operations,
            ICoordinateTransforms transforms,
            CancellationToken cancellationToken) =>
            FeatureQuery(
                new FeatureQueryContext(catalog, registry, context, service, layerId, stores, operations, transforms),
                cancellationToken));

        // Editing (ADR-0037): POST-only, per spec §9.1.6–§9.1.9.
        group.MapPost("/{service}/FeatureServer/{layerId:int}/addFeatures", (
            HttpContext context, string service, int layerId, IStoreRegistry stores, CancellationToken cancellationToken) =>
            FeatureEdit(new FeatureEditContext(catalog, registry, context, service, layerId, stores, EsriEditOperation.Add), cancellationToken));
        group.MapPost("/{service}/FeatureServer/{layerId:int}/updateFeatures", (
            HttpContext context, string service, int layerId, IStoreRegistry stores, CancellationToken cancellationToken) =>
            FeatureEdit(new FeatureEditContext(catalog, registry, context, service, layerId, stores, EsriEditOperation.Update), cancellationToken));
        group.MapPost("/{service}/FeatureServer/{layerId:int}/deleteFeatures", (
            HttpContext context, string service, int layerId, IStoreRegistry stores, CancellationToken cancellationToken) =>
            FeatureEdit(new FeatureEditContext(catalog, registry, context, service, layerId, stores, EsriEditOperation.Delete), cancellationToken));
        group.MapPost("/{service}/FeatureServer/{layerId:int}/applyEdits", (
            HttpContext context, string service, int layerId, IStoreRegistry stores, CancellationToken cancellationToken) =>
            FeatureEdit(new FeatureEditContext(catalog, registry, context, service, layerId, stores, EsriEditOperation.Apply), cancellationToken));

        // The Feature write-model operations (T-038, ADR-0058): service-level
        // query, per-layer generateRenderer, validateSQL, honest aggregation
        // rejects and the store-backed attachment surface (T-061, ADR-0066).
        // Attachment writes are admin-token-gated (ADR-0065 §3); the token is
        // the host's single admin secret, read from configuration like the
        // Esri admin projection does.
        var adminToken = app.ServiceProvider.GetService<IConfiguration>()?["Spatial:Admin:Token"];
        MapFeatureOps(group, catalog, registry, adminToken);

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
    /// Internal for the T-049 catalog-honesty tests: only served types
    /// (Feature/Map/Image) may appear — Tiles/WMS/WFS and any future
    /// GPServer-shaped service are omitted, never advertised.
    /// </summary>
    internal static async Task<List<EsriServiceEntry>> BuildServicesAsync(
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
        GeoServicesCatalog catalog, IMapRegistry registry, string service, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            var resolved = await ResolveServiceAsync(catalog, registry, service, "FeatureServer", MapService.Feature, cancellationToken);
            var layers = await ListLayersAsync(stores, resolved, cancellationToken);
            var (spatial, tables) = await SplitTablesAsync(stores, resolved, layers, cancellationToken);
            return EsriJson.Value(FeatureService.Root(spatial, tables, IsEditable(stores, resolved.Store)));
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    private static async Task<IResult> FeatureLayer(FeatureLayerContext request, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(request.Context, cancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            var resolved = await ResolveServiceAsync(request.Catalog, request.Registry, request.Service, "FeatureServer", MapService.Feature, cancellationToken);
            var description = await DescribeAsync(request.Stores, resolved, request.LayerId, cancellationToken);
            var editable = IsEditable(request.Stores, resolved.Store) && EsriObjectIdScheme.For(description).SupportsEditing;
            return EsriJson.Value(FeatureService.Layer(
                request.LayerId,
                description,
                editable,
                EsriLayerModel.IsTable(description),
                HasAttachments(request.Stores, resolved.Store)));
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    private static async Task<IResult> FeatureResource(FeatureResourceContext request, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(request.Context, cancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            var resolved = await ResolveServiceAsync(request.Catalog, request.Registry, request.Service, "FeatureServer", MapService.Feature, cancellationToken);
            var description = await DescribeAsync(request.Stores, resolved, request.LayerId, cancellationToken);
            var query = EsriFeatureQuery.Parse(parameters, EsriLayerModel.LayerCoordinateReference(description.Srid));
            var store = request.Stores.Features(resolved.Store);
            return await FeatureService.FeatureAsync(description, store, request.ObjectId, query, request.Transforms, cancellationToken);
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
            var description = await DescribeAsync(request.Stores, resolved, request.LayerId, cancellationToken);
            var query = EsriFeatureQuery.Parse(parameters, EsriLayerModel.LayerCoordinateReference(description.Srid));
            var store = request.Stores.Features(resolved.Store);
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
            var description = await DescribeAsync(request.Stores, resolved, request.LayerId, cancellationToken);
            var store = request.Stores.Features(resolved.Store);
            var editStore = EditStore(request.Stores, resolved.Store)
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

    private static bool IsEditable(IStoreRegistry stores, string store) =>
        EditStore(stores, store) is not null;

    /// <summary>Whether the layer advertises attachments: the store exposes the blob capability (T-061, ADR-0066).</summary>
    private static bool HasAttachments(IStoreRegistry stores, string store) =>
        stores.AttachmentStore(store) is not null;

    /// <summary>Resolves the service's keyed editing capability, or null when the store is read-only (ADR-0037).</summary>
    private static IFeatureEditStore? EditStore(IStoreRegistry stores, string store) =>
        stores.EditStore(store);

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
        return new ResolvedService(map.Store, layers, map.Description, map.Copyright, map.MetadataXml);
    }

    /// <summary>Whether a layer of <paramref name="kind"/> feeds <paramref name="service"/>.</summary>
    internal static bool Feeds(MapService service, MapLayerKind kind) =>
        service == MapService.Image ? kind == MapLayerKind.Image : kind == MapLayerKind.Feature;

    /// <summary>Lists the published layers: explicit for a runtime publication, whole-store (sorted) for a declared service.</summary>
    internal static async Task<IReadOnlyList<PublishedLayer>> ListLayersAsync(
        IStoreRegistry stores, ResolvedService resolved, CancellationToken cancellationToken)
    {
        if (resolved.Layers is { } layers)
        {
            return layers
                .OrderBy(layer => layer.LayerId)
                .Select(layer => new PublishedLayer(layer.LayerId, layer.Dataset, layer.Name ?? Table(layer.Dataset), layer.Style))
                .ToArray();
        }

        var catalogue = stores.Catalogue(resolved.Store);
        var datasets = (await catalogue.ListAsync(null, cancellationToken)).OrderBy(dataset => dataset.Id, StringComparer.Ordinal).ToArray();
        return datasets.Select((dataset, index) => new PublishedLayer(index, dataset.Id, dataset.Table)).ToArray();
    }

    /// <summary>
    /// Splits published layers into spatial layers and geometry-less tables
    /// (spec §9.0): each dataset is described once and routed by
    /// <see cref="EsriLayerModel.IsTable"/>. Ids are preserved from the
    /// single layer/table id space.
    /// </summary>
    internal static async Task<(IReadOnlyList<PublishedLayer> Layers, IReadOnlyList<PublishedLayer> Tables)> SplitTablesAsync(
        IStoreRegistry stores, ResolvedService resolved, IReadOnlyList<PublishedLayer> layers, CancellationToken cancellationToken)
    {
        var catalogue = stores.Catalogue(resolved.Store);
        var spatial = new List<PublishedLayer>(layers.Count);
        var tables = new List<PublishedLayer>();
        foreach (var layer in layers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var description = await catalogue.DescribeAsync(layer.Dataset, cancellationToken);
            (EsriLayerModel.IsTable(description) ? tables : spatial).Add(layer);
        }

        return (spatial, tables);
    }

    internal static async Task<DatasetDescription> DescribeAsync(
        IStoreRegistry stores, ResolvedService resolved, int layerId, CancellationToken cancellationToken)
    {
        var layers = await ListLayersAsync(stores, resolved, cancellationToken);
        var layer = layers.FirstOrDefault(candidate => candidate.Id == layerId)
            ?? throw new EsriInteropException(EsriErrorCodes.NotFound, $"Layer {layerId} does not exist in the service.");
        var catalogue = stores.Catalogue(resolved.Store);
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

/// <summary>A resolved GeoServices server: its store, its explicit layers (null means whole-store),
/// and the map's authored service description, copyright and metadata document (ADR-0068).</summary>
internal sealed record ResolvedService(
    string Store,
    IReadOnlyList<MapLayer>? Layers,
    string? Description = null,
    string? Copyright = null,
    string? MetadataXml = null);

/// <summary>The resolved services of one Feature Service layer-metadata request.</summary>
internal sealed record FeatureLayerContext(
    GeoServicesCatalog Catalog,
    IMapRegistry Registry,
    string Service,
    int LayerId,
    HttpContext Context,
    IStoreRegistry Stores);

/// <summary>The resolved services of one Feature (object) resource request.</summary>
internal sealed record FeatureResourceContext(
    GeoServicesCatalog Catalog,
    IMapRegistry Registry,
    string Service,
    int LayerId,
    long ObjectId,
    HttpContext Context,
    IStoreRegistry Stores,
    ICoordinateTransforms Transforms);

/// <summary>The resolved services of one Feature Service query request.</summary>
internal sealed record FeatureQueryContext(
    GeoServicesCatalog Catalog,
    IMapRegistry Registry,
    HttpContext Context,
    string Service,
    int LayerId,
    IStoreRegistry Stores,
    IGeometryOperations Operations,
    ICoordinateTransforms Transforms);

/// <summary>The resolved services of one Feature Service editing request.</summary>
internal sealed record FeatureEditContext(
    GeoServicesCatalog Catalog,
    IMapRegistry Registry,
    HttpContext Context,
    string Service,
    int LayerId,
    IStoreRegistry Stores,
    EsriEditOperation Operation);

/// <summary>The GeoServices catalog resource (spec §3).</summary>
internal sealed record CatalogResponse(double CurrentVersion, IReadOnlyList<string> Folders, IReadOnlyList<EsriServiceEntry> Services);

/// <summary>One catalog service entry.</summary>
internal sealed record EsriServiceEntry(string Name, string Type);
