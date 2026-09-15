using Spatial.Interop.Esri;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Providers;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The Feature write-model query handlers (T-038): the service-level
/// <c>FeatureServer/query</c> (S1), the per-layer <c>generateRenderer</c>
/// and <c>validateSQL</c>, and the honestly rejected aggregation
/// extensions. Split out of <see cref="GeoServicesEndpoints"/> so the
/// route facade keeps only mapping and the handler fan-out lives with the
/// code that uses it (ADR-0040).
/// </summary>
internal static class FeatureQueryHandlers
{
    internal static async Task<IResult> FeatureServiceQuery(
        GeoServicesCatalog catalog,
        IMapRegistry registry,
        string service,
        HttpContext context,
        IStoreRegistry stores,
        IGeometryOperations operations,
        ICoordinateTransforms transforms,
        CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            var resolved = await GeoServicesResolution.ResolveServiceAsync(catalog, registry, service, "FeatureServer", MapService.Feature, cancellationToken);
            var layers = await GeoServicesResolution.ListLayersAsync(stores, resolved, cancellationToken);
            var defs = Adapter.GeoServices.FeatureServiceQuery.ParseLayerDefs(parameters.Get("layerDefs"));
            var selected = SelectServiceLayers(layers, defs, service);
            var catalogue = stores.Catalogue(resolved.Store);
            var descriptions = new List<(PublishedLayer Layer, DatasetDescription Description)>(selected.Count);
            Spatial.Core.Geometry.CoordinateReference? fallback = null;
            foreach (var layer in selected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var description = await catalogue.DescribeAsync(layer.Dataset, cancellationToken);
                fallback ??= EsriLayerModel.LayerCoordinateReference(description.Srid);
                descriptions.Add((layer, description));
            }

            var shared = EsriFeatureQuery.Parse(parameters, fallback);
            Adapter.GeoServices.FeatureServiceQuery.RejectLayerOnlyShapes(shared);
            var targets = descriptions
                .Select(entry => new ServiceLayerQuery(
                    entry.Layer.Id,
                    entry.Description,
                    Adapter.GeoServices.FeatureServiceQuery.ForLayer(
                        shared, defs.GetValueOrDefault(entry.Layer.Id)),
                    EsriLayerModel.IsTable(entry.Description)))
                .ToArray();
            var store = stores.Features(resolved.Store);
            return await FeatureResponseWriter.ServiceQueryAsync(targets, store, shared, operations, transforms, cancellationToken);
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    private static IReadOnlyList<PublishedLayer> SelectServiceLayers(
        IReadOnlyList<PublishedLayer> layers, IReadOnlyDictionary<int, Adapter.GeoServices.FeatureServiceQuery.LayerDef> defs, string service)
    {
        if (defs.Count == 0)
        {
            return layers;
        }

        var known = layers.ToDictionary(layer => layer.Id);
        var selected = new List<PublishedLayer>(defs.Count);
        foreach (var id in defs.Keys.OrderBy(id => id))
        {
            if (!known.TryGetValue(id, out var layer))
            {
                throw GeoServicesErrors.NotFound($"Layer {id} does not exist in service '{service}'.");
            }

            selected.Add(layer);
        }

        return selected;
    }

    internal static async Task<IResult> FeatureGenerateRenderer(
        GeoServicesCatalog catalog, IMapRegistry registry, string service, int layerId,
        HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            var resolved = await GeoServicesResolution.ResolveServiceAsync(catalog, registry, service, "FeatureServer", MapService.Feature, cancellationToken);
            var description = await GeoServicesResolution.DescribeAsync(stores, resolved, layerId, cancellationToken);
            var renderer = await MapGenerateRenderer.GenerateAsync(
                stores.Features(resolved.Store), description, parameters.Get("classificationDef"), parameters.Get("where"), cancellationToken);
            return EsriJson.Value(new EsriGenerateRendererResponse(renderer));
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    internal static async Task<IResult> FeatureValidateSql(
        GeoServicesCatalog catalog, IMapRegistry registry, string service, int layerId,
        HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            var resolved = await GeoServicesResolution.ResolveServiceAsync(catalog, registry, service, "FeatureServer", MapService.Feature, cancellationToken);
            var description = await GeoServicesResolution.DescribeAsync(stores, resolved, layerId, cancellationToken);
            return EsriJson.Value(Adapter.GeoServices.FeatureValidateSql.Validate(
                description, parameters.Get("sql"), parameters.Get("sqlType")));
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    internal static async Task<IResult> UnsupportedLayerOperation(
        GeoServicesCatalog catalog,
        IMapRegistry registry,
        string service,
        int layerId,
        HttpContext context,
        IStoreRegistry stores,
        string operation,
        string guidance,
        CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            var resolved = await GeoServicesResolution.ResolveServiceAsync(catalog, registry, service, "FeatureServer", MapService.Feature, cancellationToken);
            _ = await GeoServicesResolution.DescribeAsync(stores, resolved, layerId, cancellationToken);
            throw GeoServicesErrors.Invalid($"The '{operation}' operation is not supported on service '{service}': {guidance}");
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }
}
