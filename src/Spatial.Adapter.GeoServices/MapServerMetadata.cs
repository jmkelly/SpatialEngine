using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The MapServer metadata resources (spec §4): the service root, the
/// all-layers list and one layer's metadata, each projected from the
/// publication opened by <see cref="MapServerScope"/>. Split out of
/// <see cref="MapServerEndpoints"/>, which owns the routes.
/// </summary>
internal static class MapServerMetadata
{
    public static async Task<IResult> MapServerRoot(MapServerRequest request, IEnumerable<ITileScheme> schemes)
    {
        try
        {
            var scope = await MapServerScope.OpenAsync(request);
            var infos = await scope.InfosAsync();
            return EsriJson.Value(MapServerResources.Root(
                request.Service, infos, MapServerEndpoints.MapTileScheme(schemes),
                scope.Resolved.Description, scope.Resolved.Copyright));
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    public static async Task<IResult> MapAllLayers(MapServerRequest request)
    {
        try
        {
            var scope = await MapServerScope.OpenAsync(request);
            return EsriJson.Value(MapServerResources.AllLayers(scope.Layers));
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    public static async Task<IResult> MapLayer(MapServerRequest request, int layerId)
    {
        try
        {
            var scope = await MapServerScope.OpenAsync(request);
            var info = await MapServerResources.ReadLayerAsync(scope.Store, scope.Catalogue, scope.Layer(layerId), request.CancellationToken);
            return EsriJson.Value(MapServerResources.Layer(info));
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }
}
