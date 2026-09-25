using Spatial.Contracts;
using Spatial.Contracts.Providers;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The Esri vector-tile projection (ADR-0070). It is a live MVT view over a
/// MapServer's feature layers; the same service/scheme/cache contracts serve
/// both the neutral and Esri routes. Offline exportTiles and .vtpk packaging
/// remain deliberately absent.
/// </summary>
internal static class MapVectorTileEndpoints
{
    internal static void MapRoutes(RouteGroupBuilder group, GeoServicesCatalog catalog, IMapRegistry registry)
    {
        MapOne(group, catalog, registry, "/{service}/MapServer/vectorTile/{z:int}/{y:int}/{x:int}");
        MapOne(group, catalog, registry, "/{service}/VectorTileServer/tile/{z:int}/{y:int}/{x:int}");
    }

    private static void MapOne(RouteGroupBuilder group, GeoServicesCatalog catalog, IMapRegistry registry, string route) =>
        group.MapMethods(route, ["GET", "POST"], (
            string service, [AsParameters] VectorTileAddress address, HttpContext context,
            IStoreRegistry stores, IVectorTileService encoder, ITileCache cache,
            IEnumerable<ITileScheme> schemes, CancellationToken cancellationToken) =>
            VectorTile(catalog, registry, service, address, context, stores, encoder, cache, [.. schemes], cancellationToken));

    private static async Task<IResult> VectorTile(
        GeoServicesCatalog catalog, IMapRegistry registry, string service, VectorTileAddress address,
        HttpContext context, IStoreRegistry stores, IVectorTileService encoder, ITileCache cache,
        IReadOnlyList<ITileScheme> schemes, CancellationToken cancellationToken)
    {
        try
        {
            var resolved = await GeoServicesResolution.ResolveServiceAsync(catalog, registry, service, "MapServer", MapServiceKind.MapServer, cancellationToken);
            var layers = await GeoServicesResolution.ListLayersAsync(stores, resolved, cancellationToken);
            var scheme = MapServerEndpoints.MapTileScheme(schemes)
                ?? throw GeoServicesErrors.ServiceUnavailable("No tiling scheme is configured on this host.");
            var coordinate = new TileCoordinate(address.Z, address.X, address.Y);
            if (!scheme.IsValid(coordinate))
            {
                throw GeoServicesErrors.NotFound($"Tile {address.Z}/{address.Y}/{address.X} is outside the tiling scheme.");
            }

            var sources = MapRenderEngine.Sources(stores, resolved.Store, layers, null);
            var version = MapRenderEngine.Version(service, MapRenderEngine.Style(service, layers));
            var key = new VectorTileCacheKey(scheme.Id, coordinate.Z, coordinate.X, coordinate.Y, version);
            var tile = await cache.TryGetVectorAsync(key, cancellationToken);
            var cached = tile is not null;
            if (tile is null)
            {
                var vectorLayers = layers.Select((layer, index) => new VectorTileLayer(
                    layer.Name, layer.Dataset, sources[index].Features, sources[index].Catalogue)).ToArray();
                tile = await encoder.RenderAsync(new VectorTileRequest(scheme.Bounds(coordinate), scheme.Crs, vectorLayers), cancellationToken);
                await cache.SetVectorAsync(key, tile, cancellationToken);
            }

            context.Response.Headers["X-Tile-Cached"] = cached ? "true" : "false";
            return Results.Bytes(tile!.Content, tile.MediaType);
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    private sealed class VectorTileAddress
    {
        public int Z { get; set; }
        public int Y { get; set; }
        public int X { get; set; }
    }
}
