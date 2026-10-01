using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Geometry;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// Turning a service-level <c>FeatureServer/query</c> request (S1) into the
/// per-layer targets the response writer walks: the resolved store, the
/// <c>layerDefs</c> selection, each selected layer's catalogue description,
/// the shared query, and the per-layer query projection of it. Split out of
/// <see cref="FeatureQueryHandlers"/> so the route handler stays routing and
/// the request-planning fan-out lives with the code that uses it (ADR-0040).
/// </summary>
internal static class ServiceQueryPlan
{
    /// <summary>
    /// Resolves the service, selects the requested layers and describes each
    /// one, then pairs every layer with the query it is answered under. The
    /// fallback CRS is the first selected layer's, so a shared query without
    /// an explicit <c>inSR</c> parses in the layer coordinates.
    /// </summary>
    public static async Task<ServiceQueryPlanResult> BuildAsync(
        QueryRequest request, EsriRequestParameters parameters, CancellationToken cancellationToken)
    {
        var resolved = await GeoServicesResolution.ResolveServiceAsync(
            request.Catalog, request.Registry, request.Service, "FeatureServer", MapServiceKind.FeatureServer, cancellationToken);
        var layers = await GeoServicesResolution.ListLayersAsync(request.Stores, resolved, cancellationToken);
        var defs = Adapter.GeoServices.FeatureServiceQuery.ParseLayerDefs(parameters.Get("layerDefs"));
        var selected = SelectLayers(layers, defs, request.Service);

        var catalogue = request.Stores.Catalogue(resolved.Store);
        var descriptions = new List<(PublishedLayer Layer, DatasetDescription Description)>(selected.Count);
        CoordinateReference? fallback = null;
        foreach (var layer in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var description = GeoServicesResolution.Designated(
                await catalogue.DescribeAsync(layer.Dataset, cancellationToken), layer);
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
        return new ServiceQueryPlanResult(resolved.Store, shared, targets);
    }

    /// <summary>
    /// The layers the request names, in ascending id order; every published
    /// layer when it names none. An unknown id is <c>not.found</c>.
    /// </summary>
    private static IReadOnlyList<PublishedLayer> SelectLayers(
        IReadOnlyList<PublishedLayer> layers,
        IReadOnlyDictionary<int, Adapter.GeoServices.FeatureServiceQuery.LayerDef> defs,
        string service)
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

    /// <summary>The planned service query: its store key, its shared query and its per-layer targets.</summary>
    internal sealed record ServiceQueryPlanResult(
        string Store, EsriFeatureQuery Shared, IReadOnlyList<ServiceLayerQuery> Targets);
}
