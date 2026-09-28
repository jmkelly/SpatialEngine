using Microsoft.Extensions.Logging;
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
internal static partial class MapServerTileEndpoints
{
    internal static void MapTileRoutes(RouteGroupBuilder group, GeoServicesCatalog catalog, IMapRegistry registry)
    {
        group.MapMethods("/{service}/MapServer/tile/{z:int}/{y:int}/{x:int}", ["GET", "POST"], (
            string service, [AsParameters] MapTileAddress address, HttpContext context, IStoreRegistry stores,
            IMapRenderer renderer, ITileCache cache, IEnumerable<ITileScheme> schemes,
            ILoggerFactory loggerFactory, CancellationToken cancellationToken) =>
            MapTile(new MapTileCall(
                catalog, registry, service, address, context, stores,
                new MapTileRenderDependencies(renderer, cache, [.. schemes], loggerFactory), cancellationToken)));
    }

    /// <summary>One MapServer tile request's seams: the published service, the tile address and the rendering stack.</summary>
    private sealed record MapTileCall(
        GeoServicesCatalog Catalog,
        IMapRegistry Registry,
        string Service,
        MapTileAddress Address,
        HttpContext Context,
        IStoreRegistry Stores,
        MapTileRenderDependencies Render,
        CancellationToken CancellationToken);

    private static async Task<IResult> MapTile(MapTileCall call)
    {
        var (catalog, registry, service, address, context, stores, render, cancellationToken) = call;
        var logger = render.LoggerFactory.CreateLogger(typeof(MapServerTileEndpoints));
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
            var version = MapRenderEngine.Version(
                service, style, await MapRenderEngine.DataVersionAsync(stores, resolved.Store, layers, cancellationToken));
            var key = new TileCacheKey(scheme.Id, address.Z, address.X, address.Y, RasterFormat.Png, version);
            var image = await render.Cache.TryGetAsync(key, cancellationToken);
            if (image is null)
            {
                var viewport = new RasterViewport(scheme.Bounds(coordinate), scheme.TileSize, scheme.TileSize, scheme.Crs);

                // Diagnostic seam: one Debug event per tile resolves the
                // requested address to the rendered geography, so a client
                // fetching the wrong tiles (a canvas parked at Null Island,
                // say) is distinguishable from the server rendering the
                // wrong geography.
                if (logger.IsEnabled(LogLevel.Debug))
                {
                    var bounds = viewport.Bounds;
                    LogTile(logger, service, address.Z, address.Y, address.X, scheme.Id, scheme.Crs,
                        bounds.MinX, bounds.MinY, bounds.MaxX, bounds.MaxY);
                }

                var sources = MapRenderEngine.Sources(stores, resolved.Store, layers, null);
                image = await render.Renderer.RenderAsync(
                    new MapRenderRequest(viewport, style, sources, null, RasterFormat.Png, 90, null, true, 1.0), cancellationToken);
                await render.Cache.SetAsync(key, image, cancellationToken);
            }

            GeoServicesResponses.WriteImageHeaders(context, image);
            context.Response.Headers["X-Tile-Version"] = version;
            return Results.Bytes(image.Content, image.MediaType);
        }
        catch (Exception exception)
        {
            if (exception is not OperationCanceledException)
            {
                LogTileFailed(logger, exception, service, address.Z, address.Y, address.X);
            }

            return EsriErrorMapper.Map(exception);
        }
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Debug,
        Message = "MapServer tile {Service} {Z}/{Y}/{X} via scheme '{Scheme}' ({Crs}): bounds [{MinX},{MinY},{MaxX},{MaxY}]")]
    private static partial void LogTile(
        ILogger logger, string service, int z, int y, int x, string scheme, string crs,
        double minX, double minY, double maxX, double maxY);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Warning,
        Message = "MapServer tile {Service} {Z}/{Y}/{X} failed")]
    private static partial void LogTileFailed(
        ILogger logger, Exception exception, string service, int z, int y, int x);
}

/// <summary>The MapServer tile address bound from the route via <c>[AsParameters]</c> (ADR-0040).</summary>
internal sealed class MapTileAddress
{
    public int Z { get; set; }

    public int X { get; set; }

    public int Y { get; set; }
}

/// <summary>The render seams one MapServer tile request needs, grouped so the handler stays within the parameter budget (ADR-0040).</summary>
internal sealed record MapTileRenderDependencies(
    IMapRenderer Renderer, ITileCache Cache, IReadOnlyList<ITileScheme> Schemes, ILoggerFactory LoggerFactory);
