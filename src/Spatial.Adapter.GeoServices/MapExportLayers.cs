using Spatial.Contracts;
using Spatial.Contracts.Providers;
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
        MapExportTime.ParseTimeRelation(parameters.Get("timeRelation"));
        return new MapExportLayers(
            resolved,
            selected,
            MapExportTime.ResolveTimes(
                selected, time, MapExportTime.ParseLayerTimeOptions(parameters.Get("layerTimeOptions"))));
    }
}
