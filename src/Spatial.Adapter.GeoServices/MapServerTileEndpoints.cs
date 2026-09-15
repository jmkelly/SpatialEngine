using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Geometry;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The MapServer tile route (spec §4.8, ADR-0048): a tile address resolved
/// against the configured tiling scheme, rendered through the shared
/// rendering seam and cached. Split from <see cref="MapExportEndpoints"/>
/// so the export facade keeps only the export fan-out.
/// </summary>
internal static class MapServerTileEndpoints
{
    internal static void MapTileRoutes(RouteGroupBuilder group, GeoServicesCatalog catalog, IMapRegistry registry)
    {
        group.MapMethods("/{service}/MapServer/tile/{z:int}/{y:int}/{x:int}", ["GET", "POST"], (
            string service, [AsParameters] MapTileAddress address, HttpContext context, IStoreRegistry stores,
            IMapRenderer renderer, ITileCache cache, IEnumerable<ITileScheme> schemes, CancellationToken cancellationToken) =>
            MapTile(catalog, registry, service, address, context, stores,
                new MapTileRenderDependencies(renderer, cache, [.. schemes]), cancellationToken));
    }

    private static async Task<IResult> MapTile(
        GeoServicesCatalog catalog, IMapRegistry registry, string service, MapTileAddress address,
        HttpContext context, IStoreRegistry stores, MapTileRenderDependencies render, CancellationToken cancellationToken)
    {
        try
        {
            var resolved = await GeoServicesResolution.ResolveServiceAsync(catalog, registry, service, "MapServer", MapServiceKind.MapServer, cancellationToken);
            var layers = await GeoServicesResolution.ListLayersAsync(stores, resolved, cancellationToken);
            var scheme = MapServerEndpoints.MapTileScheme(render.Schemes)
                ?? throw GeoServicesErrors.ServiceUnavailable("No tiling scheme is configured on this host.");
            var coordinate = new TileCoordinate(address.Z, address.X, address.Y);
            if (!scheme.IsValid(coordinate))
            {
                throw GeoServicesErrors.NotFound($"Tile {address.Z}/{address.Y}/{address.X} is outside the tiling scheme.");
            }

            var style = MapRenderEngine.Style(service, layers);
            var key = new TileCacheKey(scheme.Id, address.Z, address.X, address.Y, RasterFormat.Png, MapRenderEngine.Version(service, style));
            var image = await render.Cache.TryGetAsync(key, cancellationToken);
            if (image is null)
            {
                var viewport = new RasterViewport(scheme.Bounds(coordinate), scheme.TileSize, scheme.TileSize, scheme.Crs);
                var sources = MapRenderEngine.Sources(stores, resolved.Store, layers, null);
                image = await render.Renderer.RenderAsync(
                    new MapRenderRequest(viewport, style, sources, null, RasterFormat.Png, 90, null, true, 1.0), cancellationToken);
                await render.Cache.SetAsync(key, image, cancellationToken);
            }

            GeoServicesResponses.WriteImageHeaders(context, image);
            return Results.Bytes(image.Content, image.MediaType);
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }
}

/// <summary>The MapServer tile address bound from the route via <c>[AsParameters]</c> (ADR-0040).</summary>
internal sealed class MapTileAddress
{
    public int Z { get; set; }

    public int X { get; set; }

    public int Y { get; set; }
}

/// <summary>The render seams one MapServer tile request needs, grouped so the handler stays within the parameter budget (ADR-0040).</summary>
internal sealed record MapTileRenderDependencies(IMapRenderer Renderer, ITileCache Cache, IReadOnlyList<ITileScheme> Schemes);
