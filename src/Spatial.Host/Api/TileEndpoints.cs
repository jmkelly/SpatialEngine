using System.Globalization;
using Spatial.Host.Tiling;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Http;
using Spatial.PluginSdk.Providers;

namespace Spatial.Host.Api;

/// <summary>
/// The tile address and path format bound from the tile route
/// (<c>[AsParameters]</c>), so the handler's dependency list stays short
/// (ADR-0040).
/// </summary>
internal sealed class TileAddress
{
    public int Z { get; set; }

    public int X { get; set; }

    public int Y { get; set; }

    public string Format { get; set; } = "png";
}

/// <summary>
/// The tile routes (ADR-0046): one cache-aware tile, an ordered batch with
/// bounded parallelism, scheme capability discovery and explicit cache
/// invalidation. The single-tile path format is authoritative over the body;
/// the host resolves each layer's keyed store and catalogue at the edge and
/// the <see cref="TileService"/> does the cache/render orchestration.
/// </summary>
internal static class TileEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/render/tiles/{z:int}/{x:int}/{y:int}.{format}", Single)
            .Produces(StatusCodes.Status200OK)
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);
        app.MapPost("/api/render/tiles/batch", Batch)
            .Produces<TileBatchResponse>()
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest);
        app.MapGet("/api/render/tiles/capabilities", Capabilities)
            .Produces<TileCapabilitiesResponse>();
        app.MapDelete("/api/render/cache", Clear);
    }

    private static async Task<IResult> Single(
        [AsParameters] TileAddress address,
        TileRenderRequest request,
        HttpContext context,
        IStoreRegistry stores,
        TileService tiles,
        RenderingOptions options)
    {
        try
        {
            var effective = request with { Format = ParseFormat(address.Format) };
            RenderEndpoints.ValidateFormat(effective.Format, options);
            var scheme = tiles.Resolve(effective.Scheme);
            var result = await tiles.RenderAsync(
                ToSpec(effective, stores),
                Fingerprint(effective),
                new TileCoordinate(address.Z, address.X, address.Y),
                scheme,
                context.RequestAborted);
            WriteHeaders(context, result);
            return Results.Bytes(result.Image.Content, result.Image.MediaType);
        }
        catch (Exception exception)
        {
            return ErrorMapper.Map(exception);
        }
    }

    private static async Task<IResult> Batch(
        TileBatchRequest batch,
        IStoreRegistry stores,
        TileService tiles,
        RenderingOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            RenderEndpoints.ValidateFormat(batch.Request.Format, options);
            var scheme = tiles.Resolve(batch.Request.Scheme);
            var coordinates = batch.Tiles.Select(tile => new TileCoordinate(tile.Z, tile.X, tile.Y)).ToList();
            var results = await tiles.RenderBatchAsync(
                ToSpec(batch.Request, stores), Fingerprint(batch.Request), scheme, coordinates, cancellationToken);
            return Results.Ok(new TileBatchResponse(
                [.. results.Select((result, index) => ToDto(result, coordinates[index]))]));
        }
        catch (Exception exception)
        {
            return ErrorMapper.Map(exception);
        }
    }

    private static IResult Capabilities(TileService tiles) =>
        Results.Ok(new TileCapabilitiesResponse(
            tiles.DefaultScheme.Id,
            tiles.MaxTilesPerBatch,
            [.. tiles.Schemes.Select(ToDto)]));

    private static async Task<IResult> Clear(ITileCache cache, CancellationToken cancellationToken)
    {
        await cache.ClearAsync(cancellationToken);
        return Results.NoContent();
    }

    internal static TileRenderSpec ToSpec(TileRenderRequest request, IStoreRegistry stores) =>
        new(
            request.Style.GetRawText(),
            RenderEndpoints.ResolveLayers(request.Layers, stores),
            RenderEndpoints.ToImagery(request.Imagery),
            request.Format,
            request.Quality,
            request.Background,
            request.Transparent,
            request.Scale);

    internal static string Fingerprint(TileRenderRequest request)
    {
        var parts = new List<string>
        {
            request.Style.GetRawText(),
            request.Layers.Count.ToString(CultureInfo.InvariantCulture),
        };
        foreach (var layer in request.Layers)
        {
            parts.Add(layer.Dataset);
            parts.Add(layer.Store ?? string.Empty);
            parts.Add(layer.Filter ?? string.Empty);
        }

        AppendImagery(parts, request.Imagery);
        parts.Add(request.Format.ToString());
        parts.Add(request.Quality.ToString(CultureInfo.InvariantCulture));
        parts.Add(request.Background ?? string.Empty);
        parts.Add(request.Transparent ? "1" : "0");
        parts.Add(request.Scale.ToString("R", CultureInfo.InvariantCulture));
        return TileFingerprint.Compute(parts);
    }

    private static void AppendImagery(List<string> parts, IReadOnlyList<RenderImageryDto>? imagery)
    {
        parts.Add((imagery?.Count ?? 0).ToString(CultureInfo.InvariantCulture));
        if (imagery is null)
        {
            return;
        }

        foreach (var layer in imagery)
        {
            parts.Add(layer.Source);
            parts.Add(layer.Blend.ToString());
            parts.Add(layer.Opacity.ToString("R", CultureInfo.InvariantCulture));
        }
    }

    internal static void WriteHeaders(HttpContext context, TileResult result)
    {
        RenderEndpoints.WriteMetadataHeaders(context, result.Image);
        context.Response.Headers["X-Tile-Cached"] = result.Cached ? "true" : "false";
    }

    private static TileResultDto ToDto(TileResult result, TileCoordinate tile)
    {
        var image = result.Image;
        return new TileResultDto(
            tile.Z,
            tile.X,
            tile.Y,
            result.Cached,
            image.MediaType,
            image.Width,
            image.Height,
            Convert.ToBase64String(image.Content));
    }

    private static TileSchemeDto ToDto(ITileScheme scheme) =>
        new(
            scheme.Id,
            scheme.Crs,
            scheme.TileSize,
            scheme.MinZoom,
            scheme.MaxZoom,
            [.. scheme.Levels.Select(level => new TileLevelDto(level.Zoom, level.Resolution, level.ScaleDenominator))]);

    internal static RasterFormat ParseFormat(string format)
    {
        if (Enum.TryParse<RasterFormat>(format, ignoreCase: true, out var parsed)
            && string.Equals(parsed.ToString(), format, StringComparison.OrdinalIgnoreCase))
        {
            return parsed;
        }

        throw SpatialException.BadArguments($"Unknown raster format '{format}'.");
    }
}
