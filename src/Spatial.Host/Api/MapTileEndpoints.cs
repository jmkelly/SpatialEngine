using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
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
        [AsParameters] MapTileParameters parameters,
        HttpContext context,
        IMapRegistry registry,
        TileService tiles,
        RenderingOptions options)
    {
        try
        {
            var map = await registry.GetAsync(Uri.UnescapeDataString(name), context.RequestAborted);
            if (!map.Exposes(MapService.Tiles))
            {
                throw SpatialException.Missing($"Map '{map.Name}' does not expose a Tiles service.");
            }

            var request = Request(map, parameters);
            RenderEndpoints.ValidateFormat(request.Format, options);
            var scheme = tiles.Resolve(request.Scheme);
            var result = await tiles.RenderAsync(
                TileEndpoints.ToSpec(request, context.RequestServices),
                TileEndpoints.Fingerprint(request),
                new TileCoordinate(parameters.Z, parameters.X, parameters.Y),
                scheme,
                context.RequestAborted);
            TileEndpoints.WriteHeaders(context, result);
            return Results.Bytes(result.Image.Content, result.Image.MediaType);
        }
        catch (Exception exception)
        {
            return ErrorMapper.Map(exception);
        }
    }

    private static TileRenderRequest Request(Map map, MapTileParameters parameters)
    {
        var layers = map.Layers.Where(layer => layer.Kind == MapLayerKind.Feature).ToList();
        var style = MapStyle.Compose(map.Name, layers);
        return new TileRenderRequest(
            JsonDocument.Parse(style).RootElement.Clone(),
            [.. layers.Select(layer => new RenderLayerDto(layer.Dataset, layer.Store ?? map.Store))],
            Scheme: parameters.Scheme,
            Format: TileEndpoints.ParseFormat(parameters.Format),
            Quality: parameters.Quality ?? 90,
            Background: parameters.Background,
            Transparent: parameters.Transparent ?? true,
            Scale: parameters.Scale ?? 1.0);
    }
}

/// <summary>
/// The map tile address and options bound from the route and query
/// (<c>[AsParameters]</c>), so the handler's dependency list stays short
/// (ADR-0040). The path format is authoritative; the rest mirror the other
/// tile routes.
/// </summary>
internal sealed class MapTileParameters
{
    [FromRoute]
    public int Z { get; set; }

    [FromRoute]
    public int X { get; set; }

    [FromRoute]
    public int Y { get; set; }

    [FromRoute]
    public string Format { get; set; } = "png";

    [FromQuery]
    public int? Quality { get; set; }

    [FromQuery]
    public string? Background { get; set; }

    [FromQuery]
    public bool? Transparent { get; set; }

    [FromQuery]
    public double? Scale { get; set; }

    [FromQuery]
    public string? Scheme { get; set; }
}
