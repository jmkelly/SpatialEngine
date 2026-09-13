using Microsoft.AspNetCore.Http;
using Spatial.Interop.Esri;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Providers;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// Mounts the MapServer M0 resources (spec §4.0/§4.2/§4.8, ADR-0047): the
/// root, <c>layers</c>, one layer (with <c>drawingInfo</c>) and <c>query</c>,
/// for a <see cref="PublicationKind.Map"/> publication. Export, tiles,
/// identify and find are later phases of <c>map-service-plan.md</c> and are
/// intentionally absent. Query reuses <see cref="FeatureQueryEngine"/> so the
/// two servers cannot drift.
/// </summary>
internal static class MapServerEndpoints
{
    public static void Map(IEndpointRouteBuilder group)
    {
        group.MapMethods("/{service}/MapServer", ["GET", "POST"], (
            string service, HttpContext context, IPublicationRegistry registry, IServiceProvider services, CancellationToken cancellationToken) =>
            Root(service, context, registry, services, cancellationToken));
        group.MapMethods("/{service}/MapServer/layers", ["GET", "POST"], (
            string service, HttpContext context, IPublicationRegistry registry, IServiceProvider services, CancellationToken cancellationToken) =>
            AllLayers(service, context, registry, services, cancellationToken));
        group.MapMethods("/{service}/MapServer/{layerId:int}", ["GET", "POST"], (
            string service, int layerId, HttpContext context, IPublicationRegistry registry, IServiceProvider services, CancellationToken cancellationToken) =>
            Layer(service, layerId, context, registry, services, cancellationToken));
        group.MapMethods("/{service}/MapServer/{layerId:int}/query", ["GET", "POST"], (
            string service, int layerId, HttpContext context, IPublicationRegistry registry, IServiceProvider services,
            IGeometryOperations operations, ICoordinateTransforms transforms, CancellationToken cancellationToken) =>
            Query(service, layerId, context, registry, services, operations, transforms, cancellationToken));
    }

    private static async Task<IResult> Root(
        string service, HttpContext context, IPublicationRegistry registry, IServiceProvider services, CancellationToken cancellationToken)
    {
        try
        {
            await ReadRequest(context, cancellationToken);
            var resolved = await ResolveAsync(registry, service, cancellationToken);
            var described = await DescribeAllAsync(services, resolved.Publication.Store, Layers(resolved.Publication), cancellationToken);
            var references = described.Select(pair => EsriMapModel.Reference(
                pair.Layer.Id, pair.Layer.Name, EsriLayerModel.GeometryType(pair.Description.GeometryType))).ToArray();
            var srid = described.Length > 0 ? described[0].Description.Srid : 4326;
            var extent = MapService.Extent(
                await MapService.FullExtentAsync(services, resolved.Publication.Store, Layers(resolved.Publication), cancellationToken), srid);
            return EsriJson.Value(MapService.Root(resolved.Publication, references, extent, srid));
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    private static async Task<IResult> AllLayers(
        string service, HttpContext context, IPublicationRegistry registry, IServiceProvider services, CancellationToken cancellationToken)
    {
        try
        {
            await ReadRequest(context, cancellationToken);
            var resolved = await ResolveAsync(registry, service, cancellationToken);
            var described = await DescribeAllAsync(services, resolved.Publication.Store, Layers(resolved.Publication), cancellationToken);
            var references = described.Select(pair => EsriMapModel.Reference(
                pair.Layer.Id, pair.Layer.Name, EsriLayerModel.GeometryType(pair.Description.GeometryType))).ToArray();
            return EsriJson.Value(new EsriMapLayersResponse(references, []));
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    private static async Task<IResult> Layer(
        string service, int layerId, HttpContext context, IPublicationRegistry registry, IServiceProvider services, CancellationToken cancellationToken)
    {
        try
        {
            await ReadRequest(context, cancellationToken);
            var resolved = await ResolveAsync(registry, service, cancellationToken);
            var layer = Layers(resolved.Publication).FirstOrDefault(candidate => candidate.Id == layerId)
                ?? throw new EsriInteropException(EsriErrorCodes.NotFound, $"Layer {layerId} does not exist in the service.");
            var description = await DescribeAsync(services, resolved.Publication.Store, layer.Dataset, cancellationToken);
            return EsriJson.Value(MapService.Layer(layer, description));
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    private static async Task<IResult> Query(
        string service, int layerId, HttpContext context, IPublicationRegistry registry, IServiceProvider services,
        IGeometryOperations operations, ICoordinateTransforms transforms, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            var resolved = await ResolveAsync(registry, service, cancellationToken);
            var layer = Layers(resolved.Publication).FirstOrDefault(candidate => candidate.Id == layerId)
                ?? throw new EsriInteropException(EsriErrorCodes.NotFound, $"Layer {layerId} does not exist in the service.");
            var description = await DescribeAsync(services, resolved.Publication.Store, layer.Dataset, cancellationToken);
            var query = EsriFeatureQuery.Parse(parameters, EsriLayerModel.LayerCoordinateReference(description.Srid));
            var store = services.GetRequiredKeyedService<IFeatureStore>(resolved.Publication.Store);
            return await FeatureQueryEngine.QueryAsync(description, store, query, operations, transforms, cancellationToken);
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    private static async Task ReadRequest(HttpContext context, CancellationToken cancellationToken)
    {
        var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
        EsriFormat.Ensure(parameters.Get("f"));
    }

    private static async Task<ResolvedMapService> ResolveAsync(
        IPublicationRegistry registry, string service, CancellationToken cancellationToken)
    {
        Publication publication;
        try
        {
            publication = await registry.GetAsync(service, cancellationToken);
        }
        catch (SpatialException exception) when (exception.Code == SpatialException.NotFound)
        {
            throw new EsriInteropException(EsriErrorCodes.NotFound, $"Service '{service}' was not found.");
        }

        if (publication.Kind != PublicationKind.Map)
        {
            throw new EsriInteropException(EsriErrorCodes.NotFound, $"Service '{service}' was not found.");
        }

        return new ResolvedMapService(publication);
    }

    private static PublishedMapLayer[] Layers(Publication publication) =>
        publication.Layers
            .OrderBy(layer => layer.LayerId)
            .Select(layer => new PublishedMapLayer(layer.LayerId, layer.Dataset, layer.Name ?? Table(layer.Dataset), layer.Style))
            .ToArray();

    private static async Task<(PublishedMapLayer Layer, DatasetDescription Description)[]> DescribeAllAsync(
        IServiceProvider services, string store, PublishedMapLayer[] layers, CancellationToken cancellationToken)
    {
        var catalogue = services.GetRequiredKeyedService<IDataCatalogue>(store);
        var described = new List<(PublishedMapLayer, DatasetDescription)>(layers.Length);
        foreach (var layer in layers)
        {
            described.Add((layer, await catalogue.DescribeAsync(layer.Dataset, cancellationToken)));
        }

        return described.ToArray();
    }

    private static Task<DatasetDescription> DescribeAsync(
        IServiceProvider services, string store, string dataset, CancellationToken cancellationToken) =>
        services.GetRequiredKeyedService<IDataCatalogue>(store).DescribeAsync(dataset, cancellationToken);

    private static string Table(string dataset)
    {
        var dot = dataset.IndexOf('.');
        return dot < 0 ? dataset : dataset[(dot + 1)..];
    }
}

/// <summary>A resolved Map Service: the publication it projects.</summary>
internal sealed record ResolvedMapService(Publication Publication);

/// <summary>The <c>layers</c> (All Layers and Tables) response (spec §4.8).</summary>
internal sealed record EsriMapLayersResponse(IReadOnlyList<EsriMapLayerRef> Layers, IReadOnlyList<EsriMapLayerRef> Tables);
