using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The MapServer operations over the published data (spec §4):
/// per-layer <c>generateRenderer</c>, the honestly rejected image resource
/// (spec §4.7, ADR-0050), and <c>query</c>, <c>identify</c> and <c>find</c>.
/// Split out of <see cref="MapServerEndpoints"/>, which owns the routes;
/// each operation runs over the publication opened by
/// <see cref="MapServerScope"/>.
/// </summary>
internal static class MapServerOperationEndpoints
{
    /// <summary>
    /// The per-layer <c>generateRenderer</c> operation (S4, ADR-0055):
    /// server-side classification over the layer's data. This is the single
    /// map-service implementation; the feature write-model track reuses it.
    /// </summary>
    public static async Task<IResult> MapGenerateRenderer(MapServerRequest request, int layerId)
    {
        try
        {
            var scope = await MapServerScope.OpenAsync(request);
            _ = scope.Layer(layerId);
            var description = await scope.DescribeAsync(layerId);
            var renderer = await Adapter.GeoServices.MapGenerateRenderer.GenerateAsync(
                scope.Store, description, scope.Parameter("classificationDef"), scope.Parameter("where"), request.CancellationToken);
            return EsriJson.Value(new EsriGenerateRendererResponse(renderer));
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    /// <summary>
    /// The MapServer image resource (spec §4.7). It exists only for picture
    /// marker/fill symbols, whose <c>url</c> is the <c>imageId</c>. The engine's
    /// MapLibre dialect has no picture symbols and stores no symbol images, so
    /// the resource is genuinely blocked and reports a typed <c>not.found</c>
    /// rather than serving a stub (ADR-0050).
    /// </summary>
    public static async Task<IResult> MapImage(MapServerRequest request, int layerId, string imageId)
    {
        try
        {
            var (_, layers) = await MapServerScope.ResolveAsync(request);
            _ = layers.FirstOrDefault(candidate => candidate.Id == layerId)
                ?? throw GeoServicesErrors.NotFound($"Layer {layerId} does not exist in service '{request.Service}'.");
            throw GeoServicesErrors.NotFound(
                $"Image '{imageId}' is not available: MapServer image resources exist only for picture marker/fill symbols (spec §4.7), and the engine's style dialect has no picture symbols.");
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    public static async Task<IResult> MapQuery(
        MapServerRequest request, int layerId, IGeometryOperations operations, IGeometryRelations relations, ICoordinateTransforms transforms)
    {
        try
        {
            var scope = await MapServerScope.OpenAsync(request);
            var description = await scope.DescribeAsync(layerId);
            var query = EsriFeatureQuery.Parse(scope.Parameters, EsriLayerModel.LayerCoordinateReference(description.Srid));
            return await FeatureService.QueryAsync(description, scope.Store, query, operations, relations, transforms, request.CancellationToken);
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    public static async Task<IResult> MapIdentify(
        MapServerRequest request, IGeometryOperations operations, ICoordinateTransforms transforms)
    {
        try
        {
            var scope = await MapServerScope.OpenAsync(request);
            var infos = await scope.InfosAsync();
            return await MapIdentifyEngine.IdentifyAsync(
                new IdentifyRequest(
                    scope.Store, infos, MapServerResources.MapSrid(infos), operations, transforms),
                scope.Parameters, request.CancellationToken);
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    public static async Task<IResult> MapFind(MapServerRequest request, ICoordinateTransforms transforms)
    {
        try
        {
            var scope = await MapServerScope.OpenAsync(request);
            var infos = await scope.InfosAsync();
            return await MapFindEngine.FindAsync(scope.Store, infos, scope.Parameters, transforms, request.CancellationToken);
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }
}
