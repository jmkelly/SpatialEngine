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
            VectorTile(new VectorTileCall(catalog, registry, service, address, context, stores, encoder, cache, [.. schemes])));

    private static async Task<IResult> VectorTile(VectorTileCall request)
    {
        try
        {
            return await RenderAsync(request, request.Context.RequestAborted);
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    private static async Task<IResult> RenderAsync(VectorTileCall request, CancellationToken cancellationToken)
    {
        var resolved = await GeoServicesResolution.ResolveServiceAsync(
            request.Catalog, request.Registry, request.Service, "MapServer", MapServiceKind.MapServer, cancellationToken);
        var layers = await GeoServicesResolution.ListLayersAsync(request.Stores, resolved, cancellationToken);
        var scheme = MapServerEndpoints.MapTileScheme(request.Schemes)
            ?? throw GeoServicesErrors.ServiceUnavailable("No tiling scheme is configured on this host.");
        var coordinate = new TileCoordinate(request.Address.Z, request.Address.X, request.Address.Y);
        EnsureInScheme(scheme, coordinate, request.Address);

        var tile = await LoadAsync(request, resolved, layers, scheme, coordinate, cancellationToken);
        request.Context.Response.Headers["X-Tile-Cached"] = tile.Cached ? "true" : "false";
        request.Context.Response.Headers["X-Tile-Version"] = tile.Version;
        return Results.Bytes(tile.Tile!.Content, tile.Tile.MediaType);
    }

    private static void EnsureInScheme(ITileScheme scheme, TileCoordinate coordinate, VectorTileAddress address)
    {
        if (!scheme.IsValid(coordinate))
        {
            throw GeoServicesErrors.NotFound($"Tile {address.Z}/{address.Y}/{address.X} is outside the tiling scheme.");
        }
    }

    /// <summary>Serves the tile from the cache when the version key is present, otherwise renders and stores it.</summary>
    private static async Task<CachedTile> LoadAsync(
        VectorTileCall request, ResolvedService resolved, IReadOnlyList<PublishedLayer> layers,
        ITileScheme scheme, TileCoordinate coordinate, CancellationToken cancellationToken)
    {
        var sources = MapRenderEngine.Sources(request.Stores, resolved.Store, layers, null);
        var dataVersion = await MapRenderEngine.DataVersionAsync(request.Stores, resolved.Store, layers, cancellationToken);
        var version = MapRenderEngine.Version(request.Service, MapRenderEngine.Style(request.Service, layers), dataVersion);
        var key = new VectorTileCacheKey(scheme.Id, coordinate.Z, coordinate.X, coordinate.Y, version);
        var cached = await request.Cache.TryGetVectorAsync(key, cancellationToken);
        if (cached is not null)
        {
            return new CachedTile(cached, true, version);
        }

        var vectorLayers = layers.Select((layer, index) => new VectorTileLayer(
            layer.Name, layer.Dataset, sources[index].Features, sources[index].Catalogue)).ToArray();
        var tile = await request.Encoder.RenderAsync(
            new VectorTileRequest(scheme.Bounds(coordinate), scheme.Crs, vectorLayers), cancellationToken);
        await request.Cache.SetVectorAsync(key, tile, cancellationToken);
        return new CachedTile(tile, false, version);
    }

    /// <summary>One resolved tile call: the route's bound services and the address it addresses.</summary>
    private sealed record VectorTileCall(
        GeoServicesCatalog Catalog,
        IMapRegistry Registry,
        string Service,
        VectorTileAddress Address,
        HttpContext Context,
        IStoreRegistry Stores,
        IVectorTileService Encoder,
        ITileCache Cache,
        IReadOnlyList<ITileScheme> Schemes);

    /// <summary>One served tile, its cache disposition and the key it was stored under (ADR-0083).</summary>
    private sealed record CachedTile(VectorTile Tile, bool Cached, string Version);

    private sealed class VectorTileAddress
    {
        public int Z { get; set; }
        public int Y { get; set; }
        public int X { get; set; }
    }
}
