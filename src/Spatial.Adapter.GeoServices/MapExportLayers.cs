using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Esri.Codec;


namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The layer surface one MapServer export draws (spec §4.0.4, ADR-0048):
/// the resolved publication, the layers the request selected after
/// <c>dynamicLayers</c>/<c>layers</c>/<c>layerOption</c> are applied, and the
/// per-layer time window the request resolved. Split out of
/// <see cref="MapExportPlanner"/> — which frames the viewport — so
/// selecting what to export is named apart from framing how.
/// </summary>
internal sealed record MapExportLayers(
    ResolvedService Resolved,
    IReadOnlyList<PublishedLayer> Selected,
    IReadOnlyDictionary<int, MapTimeExtent>? Times)
{
    /// <summary>Resolves the publication, its selected layers and the request's temporal selection.</summary>
    public static async Task<MapExportLayers> ResolveAsync(
        GeoServicesCatalog catalog,
        IMapRegistry registry,
        IStoreRegistry stores,
        string service,
        EsriRequestParameters parameters,
        CancellationToken cancellationToken)
    {
        var resolved = await GeoServicesResolution.ResolveServiceAsync(
            catalog, registry, service, "MapServer", MapServiceKind.MapServer, cancellationToken);
        var effective = MapDynamicLayers.Apply(
            await GeoServicesResolution.ListLayersAsync(stores, resolved, cancellationToken),
            MapDynamicLayers.Parse(parameters.Get("dynamicLayers")),
            service);
        var selected = MapLayerSelection.Select(effective, parameters.Get("layers"), parameters.Get("layerOption"));
        var time = EsriFeatureQuery.ParseTime(parameters.Get("time"));
        var relation = MapExportTime.ParseTimeRelation(parameters.Get("timeRelation"));
        if (relation is { } requested and not TemporalRelation.Overlaps)
        {
            // A relation that needs a feature temporal extent is refused before
            // anything is rendered, which is where the layer's designation is
            // cheapest to read: only a request that asked for one describes
            // the datasets at all (ADR-0175 §6).
            MapExportTime.RequireDesignated(requested, await DesignationsAsync(
                stores, resolved.Store, selected, cancellationToken));
        }

        return new MapExportLayers(
            resolved,
            selected,
            MapExportTime.ResolveTimes(
                selected, time, MapExportTime.ParseLayerTimeOptions(parameters.Get("layerTimeOptions")), relation));
    }

    /// <summary>What each selected layer's schema designates as the dates bounding a feature.</summary>
    private static async Task<IReadOnlyList<MapDesignation>> DesignationsAsync(
        IStoreRegistry stores, string store, IReadOnlyList<PublishedLayer> layers, CancellationToken cancellationToken)
    {
        var catalogue = MapServerEndpoints.Catalogue(stores, store);
        var designations = new List<MapDesignation>(layers.Count);
        foreach (var layer in layers)
        {
            var description = await catalogue.DescribeAsync(layer.Dataset, cancellationToken);
            designations.Add(new MapDesignation(layer.Id, layer.Name, description.TimeFields));
        }

        return designations;
    }
}
