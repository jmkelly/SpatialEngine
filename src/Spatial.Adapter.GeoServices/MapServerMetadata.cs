using Microsoft.Extensions.Logging;
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
internal static partial class MapServerMetadata
{
    public static async Task<IResult> MapServerRoot(
        MapServerRequest request,
        IEnumerable<ITileScheme> schemes,
        ICoordinateTransforms transforms,
        ILoggerFactory loggerFactory)
    {
        try
        {
            var scope = await MapServerScope.OpenAsync(request);
            var infos = await scope.InfosAsync();
            var root = MapServerResources.Root(
                request.Service, infos, MapServerEndpoints.MapTileScheme(schemes),
                scope.Resolved.Description, scope.Resolved.Copyright,
                transforms, request.CancellationToken);

            // Diagnostic seam: the advertised service SR, full extent and
            // tile-scheme SR must agree for a fused-cache service — a client
            // deriving tile indices from contradictory metadata fetches the
            // wrong tiles while every request still returns 200.
            var logger = loggerFactory.CreateLogger(typeof(MapServerMetadata));
            if (logger.IsEnabled(LogLevel.Information))
            {
                var extent = root.FullExtent;
                LogRoot(
                    logger, request.Service, root.SpatialReference?.Wkid,
                    extent?.Xmin, extent?.Ymin, extent?.Xmax, extent?.Ymax,
                    extent?.SpatialReference?.Wkid, root.SingleFusedMapCache,
                    root.TileInfo?.SpatialReference?.Wkid);
            }

            return EsriJson.Value(root);
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

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "MapServer root {Service} advertises spatial reference {SrWkid}, full extent [{MinX},{MinY},{MaxX},{MaxY}] ({ExtentWkid}), fused cache {FusedCache}, tileInfo SR {TileWkid}")]
    private static partial void LogRoot(
        ILogger logger, string service, int? srWkid,
        double? minX, double? minY, double? maxX, double? maxY, int? extentWkid,
        bool fusedCache, int? tileWkid);
}
