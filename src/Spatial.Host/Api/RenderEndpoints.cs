using System.Globalization;
using Spatial.Core.Geometry;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Http;

namespace Spatial.Host.Api;

/// <summary>
/// The raster render routes (ADR-0044): a one-off styled export and the
/// capability description. The host maps the primitive wire DTO to core-typed
/// contracts, validates the advertised format, resolves each layer's keyed
/// store and catalogue at the edge, and passes them to the renderer.
/// </summary>
internal static class RenderEndpoints
{
    private static readonly string[] PixelFormats = ["rgba8888", "rgb888"];
    private static readonly string[] BlendModes = ["over", "multiply", "screen", "darken", "lighten"];

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/render", Render)
            .Produces(StatusCodes.Status200OK)
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);
        app.MapGet("/api/render/capabilities", Capabilities)
            .Produces<RenderCapabilitiesResponse>();
    }

    private static async Task<IResult> Render(
        RenderRequest request,
        HttpContext context,
        IServiceProvider services,
        IMapRenderer renderer,
        RenderingOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            ValidateFormat(request.Format, options);
            var mapRequest = new MapRenderRequest(
                ToViewport(request.Viewport),
                request.Style.GetRawText(),
                ResolveLayers(request.Layers, services),
                ToImagery(request.Imagery),
                request.Format,
                request.Quality,
                request.Background,
                request.Transparent,
                request.Scale);
            var image = await renderer.RenderAsync(mapRequest, cancellationToken);
            WriteMetadataHeaders(context, image);
            return Results.Bytes(image.Content, image.MediaType);
        }
        catch (Exception exception)
        {
            return ErrorMapper.Map(exception);
        }
    }

    private static void WriteMetadataHeaders(HttpContext context, RasterImage image)
    {
        context.Response.Headers["X-Raster-Width"] = image.Width.ToString(CultureInfo.InvariantCulture);
        context.Response.Headers["X-Raster-Height"] = image.Height.ToString(CultureInfo.InvariantCulture);
        context.Response.Headers["X-Raster-Format"] = image.Format.ToString();
    }

    private static IResult Capabilities(RenderingOptions options, ImageryOptions imagery) =>
        Results.Ok(new RenderCapabilitiesResponse(
            options.Formats,
            PixelFormats,
            BlendModes,
            options.MaxPixels,
            [.. imagery.Sources.Select(source => new ImagerySourceDto(source.Name))]));

    private static void ValidateFormat(RasterFormat format, RenderingOptions options)
    {
        foreach (var advertised in options.Formats)
        {
            if (string.Equals(advertised, format.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        throw SpatialException.BadArguments($"Format '{format}' is not configured; see /api/render/capabilities.");
    }

    private static RasterViewport ToViewport(ViewportDto dto)
    {
        try
        {
            return new RasterViewport(new Envelope(dto.MinX, dto.MinY, dto.MaxX, dto.MaxY), dto.Width, dto.Height, dto.Crs);
        }
        catch (ArgumentException exception)
        {
            throw SpatialException.BadArguments($"Invalid viewport bounds: {exception.Message}");
        }
    }

    private static List<MapLayerSource> ResolveLayers(IReadOnlyList<RenderLayerDto> layers, IServiceProvider services)
    {
        var resolved = new List<MapLayerSource>(layers.Count);
        foreach (var layer in layers)
        {
            var store = layer.Store ?? StoreEndpoints.Demo;
            resolved.Add(new MapLayerSource(
                layer.Dataset,
                StoreEndpoints.ResolveFeatures(services, store),
                StoreEndpoints.ResolveCatalogue(services, store),
                layer.Filter));
        }

        return resolved;
    }

    private static List<RasterSourceLayer> ToImagery(IReadOnlyList<RenderImageryDto>? imagery)
    {
        var layers = new List<RasterSourceLayer>();
        if (imagery is null)
        {
            return layers;
        }

        foreach (var source in imagery)
        {
            layers.Add(new RasterSourceLayer(source.Source, source.Blend, source.Opacity));
        }

        return layers;
    }
}
