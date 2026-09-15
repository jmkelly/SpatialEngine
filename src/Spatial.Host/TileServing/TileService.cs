using Spatial.PluginSdk;

namespace Spatial.Host.TileServing;

/// <summary>
/// The tile-invariant half of a render request (ADR-0046): everything except
/// the viewport, which the <see cref="TileService"/> derives per tile. Keeps
/// a batch from carrying a single (wrong) viewport for every tile.
/// </summary>
internal sealed record TileRenderSpec(
    string Style,
    IReadOnlyList<MapLayerSource> Layers,
    IReadOnlyList<RasterSourceLayer> Imagery,
    RasterFormat Format,
    int Quality,
    string? Background,
    bool Transparent,
    double Scale)
{
    public MapRenderRequest Request(RasterViewport viewport) =>
        new(viewport, Style, Layers, Imagery, Format, Quality, Background, Transparent, Scale);
}

/// <summary>One rendered tile plus whether it came from the cache.</summary>
internal sealed record TileResult(RasterImage Image, bool Cached);

/// <summary>
/// The tile orchestration (ADR-0046): resolve the scheme, put the tile
/// address through the cache and, on a miss, derive the tile viewport, render
/// it and store it. A batch renders an ordered tile list with bounded
/// parallelism. No projection math lives here — the scheme owns it — and no
/// pixels are touched — the renderer owns them.
/// </summary>
internal sealed class TileService
{
    private readonly IMapRenderer _renderer;
    private readonly ITileCache _cache;
    private readonly Dictionary<string, ITileScheme> _schemes;
    private readonly TileOptions _options;

    public TileService(IMapRenderer renderer, IEnumerable<ITileScheme> schemes, ITileCache cache, TileOptions options)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(schemes);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(options);
        _renderer = renderer;
        _cache = cache;
        _options = options;
        _schemes = new Dictionary<string, ITileScheme>(StringComparer.OrdinalIgnoreCase);
        foreach (var scheme in schemes)
        {
            _schemes[scheme.Id] = scheme;
        }

        if (_schemes.Count == 0)
        {
            throw new InvalidOperationException("No tiling scheme is registered.");
        }

        DefaultScheme = ResolveDefault();
    }

    /// <summary>The scheme used when a request names none; validated at composition time.</summary>
    public ITileScheme DefaultScheme { get; }

    /// <summary>Every registered scheme, for the capabilities route.</summary>
    public IReadOnlyCollection<ITileScheme> Schemes => _schemes.Values;

    public int MaxTilesPerBatch => _options.MaxTilesPerBatch;

    /// <summary>Resolves a request's scheme id, falling back to <see cref="DefaultScheme"/>.</summary>
    public ITileScheme Resolve(string? id) =>
        string.IsNullOrWhiteSpace(id) ? DefaultScheme : Lookup(id);

    /// <summary>Renders one tile through the cache.</summary>
    public async Task<TileResult> RenderAsync(
        TileRenderSpec spec,
        string version,
        TileCoordinate tile,
        ITileScheme scheme,
        CancellationToken cancellationToken)
    {
        var viewport = Viewport(tile, scheme);
        var key = new TileCacheKey(scheme.Id, tile.Z, tile.X, tile.Y, spec.Format, version);
        if (await _cache.TryGetAsync(key, cancellationToken).ConfigureAwait(false) is { } cached)
        {
            return new TileResult(cached, true);
        }

        var image = await _renderer.RenderAsync(spec.Request(viewport), cancellationToken).ConfigureAwait(false);
        await _cache.SetAsync(key, image, cancellationToken).ConfigureAwait(false);
        return new TileResult(image, false);
    }

    /// <summary>Renders an ordered tile list with bounded parallelism, preserving request order in the result.</summary>
    public async Task<IReadOnlyList<TileResult>> RenderBatchAsync(
        TileRenderSpec spec,
        string version,
        ITileScheme scheme,
        IReadOnlyList<TileCoordinate> tiles,
        CancellationToken cancellationToken)
    {
        if (tiles.Count == 0)
        {
            throw SpatialException.BadArguments("A tile batch requires at least one tile.");
        }

        if (tiles.Count > _options.MaxTilesPerBatch)
        {
            throw SpatialException.BadArguments(
                FormattableString.Invariant($"The batch lists {tiles.Count} tiles, above the {_options.MaxTilesPerBatch}-tile limit."));
        }

        var results = new TileResult[tiles.Count];
        var concurrency = _options.Concurrency > 0 ? _options.Concurrency : Environment.ProcessorCount;
        await Parallel.ForEachAsync(
            Enumerable.Range(0, tiles.Count),
            new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = cancellationToken },
            async (index, token) =>
                results[index] = await RenderAsync(spec, version, tiles[index], scheme, token).ConfigureAwait(false))
            .ConfigureAwait(false);
        return results;
    }

    private static RasterViewport Viewport(TileCoordinate tile, ITileScheme scheme)
    {
        if (!scheme.IsValid(tile))
        {
            throw SpatialException.BadArguments(
                FormattableString.Invariant($"Tile {tile.Z}/{tile.X}/{tile.Y} is outside the {scheme.Id} scheme."));
        }

        return new RasterViewport(scheme.Bounds(tile), scheme.TileSize, scheme.TileSize, scheme.Crs);
    }

    private ITileScheme Lookup(string id) =>
        _schemes.TryGetValue(id, out var scheme)
            ? scheme
            : throw SpatialException.BadArguments($"Unknown tile scheme '{id}'.");

    private ITileScheme ResolveDefault()
    {
        var id = _options.DefaultScheme;
        if (_schemes.TryGetValue(id, out var scheme))
        {
            return scheme;
        }

        throw new InvalidOperationException($"The configured default tile scheme '{id}' is not registered.");
    }
}
