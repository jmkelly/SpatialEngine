using System.Text.Json;
using Spatial.Host.Tiling;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Http;
using Spatial.PluginSdk.Providers;

namespace Spatial.Host.Api;

/// <summary>
/// The map tile route (ADR-0052 §3, ADR-0046): <c>GET /api/maps/{name}/tiles/
/// {z}/{x}/{y}.{format}</c> renders a map's feature layers, composed from its
/// persisted per-layer styles, through the shared <see cref="TileService"/>.
/// The path format is authoritative; the query options mirror the other tile
/// routes. A map that does not expose the Tiles service is not-found.
/// </summary>
internal static class MapTileEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/maps/{name}/tiles/{z:int}/{x:int}/{y:int}.{format}", Tile)
            .Produces(StatusCodes.Status200OK)
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);
    }

    private static async Task<IResult> Tile(
        string name,
        [AsParameters] TileAddress address,
        HttpContext context,
        IServiceProvider services,
        IMapRegistry registry,
        TileService tiles,
        RenderingOptions options,
        int quality = 90,
        string? background = null,
        bool transparent = true,
        double scale = 1.0,
        string? scheme = null)
    {
        try
        {
            var map = await registry.GetAsync(Uri.UnescapeDataString(name), context.RequestAborted);
            if (!map.Exposes(MapService.Tiles))
            {
                throw SpatialException.Missing($"Map '{map.Name}' does not expose a Tiles service.");
            }

            var request = Request(map, address.Format, quality, background, transparent, scale, scheme);
            RenderEndpoints.ValidateFormat(request.Format, options);
            var tileScheme = tiles.Resolve(request.Scheme);
            var result = await tiles.RenderAsync(
                TileEndpoints.ToSpec(request, services),
                TileEndpoints.Fingerprint(request),
                new TileCoordinate(address.Z, address.X, address.Y),
                tileScheme,
                context.RequestAborted);
            TileEndpoints.WriteHeaders(context, result);
            return Results.Bytes(result.Image.Content, result.Image.MediaType);
        }
        catch (Exception exception)
        {
            return ErrorMapper.Map(exception);
        }
    }

    private static TileRenderRequest Request(
        Map map, string format, int quality, string? background, bool transparent, double scale, string? scheme)
    {
        var layers = map.Layers.Where(layer => layer.Kind == MapLayerKind.Feature).ToList();
        var style = MapStyle.Compose(map.Name, layers);
        return new TileRenderRequest(
            JsonDocument.Parse(style).RootElement.Clone(),
            [.. layers.Select(layer => new RenderLayerDto(layer.Dataset, layer.Store ?? map.Store))],
            Scheme: scheme,
            Format: TileEndpoints.ParseFormat(format),
            Quality: quality,
            Background: background,
            Transparent: transparent,
            Scale: scale);
    }
}
