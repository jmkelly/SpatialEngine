using Microsoft.AspNetCore.Http;
using Spatial.Interop.Esri;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Providers;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// Mounts the GeoServices REST route group (ADR-0035): the catalog, the
/// Geometry Service and read-only Feature Servers. Every handler negotiates
/// <c>f=json</c>, reads the merged request parameters and maps failures to
/// the Esri error envelope.
/// </summary>
public static class GeoServicesEndpoints
{
    /// <summary>Maps the facade at <see cref="GeoServicesOptions.Root"/>.</summary>
    public static void Map(IEndpointRouteBuilder app, GeoServicesOptions options)
    {
        var catalog = new GeoServicesCatalog(options);
        var group = app.MapGroup(catalog.Root);

        group.MapGet(string.Empty, (string? f) => Catalog(catalog, f));
        group.MapGet("/Geometry/GeometryServer", () => GeometryService.Info());
        group.MapMethods("/Geometry/GeometryServer/{operation}", ["GET", "POST"], GeometryOperation);

        group.MapGet("/{service}/FeatureServer", (string service, string? f, IServiceProvider services, CancellationToken cancellationToken) =>
            FeatureServerRoot(catalog, service, f, services, cancellationToken));
        group.MapGet("/{service}/FeatureServer/{layerId:int}", (string service, int layerId, string? f, IServiceProvider services, CancellationToken cancellationToken) =>
            FeatureLayer(catalog, service, layerId, f, services, cancellationToken));
        group.MapMethods("/{service}/FeatureServer/{layerId:int}/query", ["GET", "POST"], (
            HttpContext context,
            string service,
            int layerId,
            IServiceProvider services,
            IGeometryOperations operations,
            ICoordinateTransforms transforms,
            CancellationToken cancellationToken) =>
            FeatureQuery(
                new FeatureQueryContext(catalog, context, service, layerId, services, operations, transforms),
                cancellationToken));
    }

    private static IResult Catalog(GeoServicesCatalog catalog, string? format)
    {
        try
        {
            EsriFormat.Ensure(format);
            return EsriJson.Value(new CatalogResponse(
                10.0,
                [],
                catalog.Services.Select(service => new EsriServiceEntry(service.Name, service.Type)).ToArray()));
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    private static async Task<IResult> GeometryOperation(
        HttpContext context, string operation, IServiceProvider services, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            var capabilities = new GeometryServiceCapabilities(
                services.GetRequiredService<IGeometryOperations>(),
                services.GetRequiredService<IGeometryMeasures>(),
                services.GetRequiredService<IGeometryProcessing>(),
                services.GetRequiredService<IGeometryRelations>(),
                services.GetRequiredService<ICoordinateTransforms>());
            return GeometryService.Dispatch(operation, parameters, capabilities, cancellationToken);
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    private static async Task<IResult> FeatureServerRoot(
        GeoServicesCatalog catalog, string service, string? f, IServiceProvider services, CancellationToken cancellationToken)
    {
        try
        {
            EsriFormat.Ensure(f);
            var datasets = await ListDatasetsAsync(services, catalog, service, cancellationToken);
            return EsriJson.Value(FeatureService.Root(datasets));
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    private static async Task<IResult> FeatureLayer(
        GeoServicesCatalog catalog, string service, int layerId, string? f, IServiceProvider services, CancellationToken cancellationToken)
    {
        try
        {
            EsriFormat.Ensure(f);
            var datasets = await ListDatasetsAsync(services, catalog, service, cancellationToken);
            var description = await DescribeAsync(services, catalog, service, datasets, layerId, cancellationToken);
            return EsriJson.Value(FeatureService.Layer(layerId, description));
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
            var datasets = await ListDatasetsAsync(request.Services, request.Catalog, request.Service, cancellationToken);
            var description = await DescribeAsync(request.Services, request.Catalog, request.Service, datasets, request.LayerId, cancellationToken);
            var query = EsriFeatureQuery.Parse(parameters, EsriLayerModel.LayerCoordinateReference(description.Srid));
            var store = request.Services.GetRequiredKeyedService<IFeatureStore>(ResolveFeature(request.Catalog, request.Service).Store);
            return await FeatureService.QueryAsync(description, store, query, request.Operations, request.Transforms, cancellationToken);
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    private static async Task<IReadOnlyList<DatasetSummary>> ListDatasetsAsync(
        IServiceProvider services, GeoServicesCatalog catalog, string service, CancellationToken cancellationToken)
    {
        var catalogue = Catalogue(services, catalog, service);
        var datasets = await catalogue.ListAsync(null, cancellationToken);
        return datasets.OrderBy(dataset => dataset.Id, StringComparer.Ordinal).ToArray();
    }

    private static async Task<DatasetDescription> DescribeAsync(
        IServiceProvider services,
        GeoServicesCatalog catalog,
        string service,
        IReadOnlyList<DatasetSummary> datasets,
        int layerId,
        CancellationToken cancellationToken)
    {
        if (layerId < 0 || layerId >= datasets.Count)
        {
            throw new EsriInteropException(EsriErrorCodes.NotFound, $"Layer {layerId} does not exist in service '{service}'.");
        }

        var catalogue = Catalogue(services, catalog, service);
        return await catalogue.DescribeAsync(datasets[layerId].Id, cancellationToken);
    }

    private static IDataCatalogue Catalogue(IServiceProvider services, GeoServicesCatalog catalog, string service) =>
        services.GetRequiredKeyedService<IDataCatalogue>(ResolveFeature(catalog, service).Store);

    private static GeoServicesServiceEntry ResolveFeature(GeoServicesCatalog catalog, string service)
    {
        if (catalog.TryGet(service, out var entry) && entry.Type == "FeatureServer")
        {
            return entry;
        }

        throw new EsriInteropException(EsriErrorCodes.NotFound, $"Service '{service}' was not found.");
    }
}

/// <summary>The resolved services of one Feature Service query request.</summary>
internal sealed record FeatureQueryContext(
    GeoServicesCatalog Catalog,
    HttpContext Context,
    string Service,
    int LayerId,
    IServiceProvider Services,
    IGeometryOperations Operations,
    ICoordinateTransforms Transforms);

/// <summary>The GeoServices catalog resource (spec §3).</summary>
internal sealed record CatalogResponse(double CurrentVersion, IReadOnlyList<string> Folders, IReadOnlyList<EsriServiceEntry> Services);

/// <summary>One catalog service entry.</summary>
internal sealed record EsriServiceEntry(string Name, string Type);
