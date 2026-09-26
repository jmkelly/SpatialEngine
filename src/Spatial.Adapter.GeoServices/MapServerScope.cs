using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// One opened MapServer request (spec §4): the negotiated Esri parameters,
/// the resolved publication and its published layers. Every MapServer
/// resource opens the request the same way — read the parameters, require
/// <c>f=json</c>, resolve the MapServer publication and list its layers — so
/// the route handlers share this step instead of repeating it. Split out of
/// the resource classes so the resolution fan-out lives in one place.
/// </summary>
internal sealed record MapServerScope(
    MapServerRequest Request,
    EsriRequestParameters Parameters,
    ResolvedService Resolved,
    IReadOnlyList<PublishedLayer> Layers)
{
    /// <summary>Reads the parameters, negotiates <c>f=json</c> and resolves the publication.</summary>
    public static async Task<MapServerScope> OpenAsync(MapServerRequest request)
    {
        var parameters = await EsriRequestParameters.ReadAsync(request.Context, request.CancellationToken);
        EsriFormat.Ensure(parameters.Get("f"));
        var (resolved, layers) = await ResolveAsync(request);
        return new MapServerScope(request, parameters, resolved, layers);
    }

    /// <summary>Resolves the publication and its layers without negotiating the response format.</summary>
    public static async Task<(ResolvedService Resolved, IReadOnlyList<PublishedLayer> Layers)> ResolveAsync(
        MapServerRequest request)
    {
        var resolved = await GeoServicesResolution.ResolveServiceAsync(
            request.Catalog, request.Registry, request.Service, "MapServer", MapServiceKind.MapServer, request.CancellationToken);
        var layers = await GeoServicesResolution.ListLayersAsync(request.Stores, resolved, request.CancellationToken);
        return (resolved, layers);
    }

    /// <summary>The publication's feature store.</summary>
    public IFeatureStore Store => MapServerEndpoints.Store(Request.Stores, Resolved.Store);

    /// <summary>The publication's catalogue.</summary>
    public IDataCatalogue Catalogue => MapServerEndpoints.Catalogue(Request.Stores, Resolved.Store);

    /// <summary>One published layer; an unknown id is typed <c>not.found</c>, never silently dropped.</summary>
    public PublishedLayer Layer(int layerId) =>
        Layers.FirstOrDefault(candidate => candidate.Id == layerId)
        ?? throw GeoServicesErrors.NotFound($"Layer {layerId} does not exist in service '{Request.Service}'.");

    /// <summary>Every published layer's served metadata.</summary>
    public Task<IReadOnlyList<MapLayerInfo>> InfosAsync() =>
        MapServerResources.ReadLayersAsync(Store, Catalogue, Layers, Request.CancellationToken);

    /// <summary>One published layer's discovered description.</summary>
    public Task<DatasetDescription> DescribeAsync(int layerId) =>
        GeoServicesResolution.DescribeAsync(Request.Stores, Resolved, layerId, Request.CancellationToken);

    /// <summary>The value of one Esri request parameter.</summary>
    public string? Parameter(string name) => Parameters.Get(name);
}
