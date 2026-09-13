using Spatial.PluginSdk;
using Spatial.PluginSdk.Http;
using Spatial.PluginSdk.Providers;

namespace Spatial.Host.Api;

/// <summary>
/// The publication render route (ADR-0047): renders a named publication's
/// datasets using the per-layer styles persisted with it, so a persisted
/// style takes effect headlessly (no workbench, no inline style document).
/// The host resolves the publication and each layer's keyed store at the
/// edge and passes a composed <see cref="MapRenderRequest"/> to the renderer;
/// the renderer never sees a publication (ADR-0005/ADR-0033).
/// </summary>
internal static class PublicationRenderEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/publications/{name}/render", Render)
            .Produces(StatusCodes.Status200OK)
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);
    }

    private static async Task<IResult> Render(
        string name,
        PublicationRenderRequest request,
        HttpContext context,
        IServiceProvider services,
        IPublicationRegistry registry,
        IMapRenderer renderer,
        RenderingOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            RenderEndpoints.ValidateFormat(request.Format, options);
            var publication = await registry.GetAsync(Uri.UnescapeDataString(name), cancellationToken);
            var mapRequest = new MapRenderRequest(
                RenderEndpoints.ToViewport(request.Viewport),
                PublicationStyleComposer.Compose(publication),
                ResolveLayers(publication, services),
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

    private static List<MapLayerSource> ResolveLayers(Publication publication, IServiceProvider services)
    {
        var layers = publication.Layers
            .Select(layer => new RenderLayerDto(layer.Dataset, publication.Store))
            .ToList();
        return RenderEndpoints.ResolveLayers(layers, services);
    }
}
