using Spatial.PluginSdk;
using Spatial.PluginSdk.Http;
using Spatial.PluginSdk.Providers;

namespace Spatial.Host.Api;

/// <summary>
/// The map render route (ADR-0053 §4, evolving ADR-0047): renders a named
/// map's feature layers using the per-layer styles persisted with it, so a
/// persisted style takes effect headlessly (no workbench, no inline style
/// document). The host resolves the map and each layer's keyed store at the
/// edge and passes a composed <see cref="MapRenderRequest"/> to the renderer;
/// the renderer never sees a map (ADR-0005/ADR-0033). The pre-ADR-0053
/// <c>/api/publications/{name}/render</c> route is a deprecated alias.
/// </summary>
internal static class MapRenderEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/maps/{name}/render", Render)
            .Produces(StatusCodes.Status200OK)
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);
        app.MapPost("/api/publications/{name}/render", Render);
    }

    private static async Task<IResult> Render(
        string name,
        MapRenderRequestDto request,
        HttpContext context,
        IServiceProvider services,
        IMapRegistry registry,
        IMapRenderer renderer,
        RenderingOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            RenderEndpoints.ValidateFormat(request.Format, options);
            var map = await registry.GetAsync(Uri.UnescapeDataString(name), cancellationToken);
            var mapRequest = new MapRenderRequest(
                RenderEndpoints.ToViewport(request.Viewport),
                MapStyle.Compose(map),
                ResolveLayers(map, services),
                RenderEndpoints.ToImagery(request.Imagery),
                request.Format,
                request.Quality,
                request.Background,
                request.Transparent,
                request.Scale);
            var image = await renderer.RenderAsync(mapRequest, cancellationToken);
            RenderEndpoints.WriteMetadataHeaders(context, image);
            return Results.Bytes(image.Content, image.MediaType);
        }
        catch (Exception exception)
        {
            return ErrorMapper.Map(exception);
        }
    }

    private static List<MapLayerSource> ResolveLayers(Map map, IServiceProvider services)
    {
        var layers = map.Layers
            .Where(layer => layer.Kind == MapLayerKind.Feature)
            .Select(layer => new RenderLayerDto(layer.Dataset, layer.Store ?? map.Store))
            .ToList();
        return RenderEndpoints.ResolveLayers(layers, services);
    }
}
