using System.Security.Cryptography;
using System.Text;
using Spatial.Contracts;
using Spatial.Contracts.Providers;

namespace Spatial.Host.TileServing;

/// <summary>One vector tile and its cache disposition.</summary>
internal sealed record VectorTileResult(VectorTile Tile, bool Cached);

/// <summary>
/// Cache-aware orchestration for live vector tiles. Scheme lookup, address
/// validation and cache identity live here; the MVT implementation owns
/// feature reads, reprojection and protobuf encoding.
/// </summary>
internal sealed class VectorTileService
{
    private readonly IVectorTileService _encoder;
    private readonly ITileCache _cache;
    private readonly Dictionary<string, ITileScheme> _schemes;
    private readonly string _defaultScheme;

    public VectorTileService(IVectorTileService encoder, IEnumerable<ITileScheme> schemes, ITileCache cache, TileOptions options)
    {
        _encoder = encoder;
        _cache = cache;
        _defaultScheme = options.DefaultScheme;
        _schemes = schemes.ToDictionary(scheme => scheme.Id, StringComparer.OrdinalIgnoreCase);
        if (_schemes.Count == 0)
        {
            throw new InvalidOperationException("No tiling scheme is registered.");
        }
        if (!_schemes.ContainsKey(_defaultScheme))
        {
            throw new InvalidOperationException($"The configured default tile scheme '{_defaultScheme}' is not registered.");
        }
    }

    public ITileScheme Resolve(string? id)
    {
        var key = string.IsNullOrWhiteSpace(id) ? _defaultScheme : id;
        return _schemes.TryGetValue(key, out var scheme)
            ? scheme
            : throw SpatialException.BadArguments($"Unknown tile scheme '{key}'.");
    }

    public async Task<VectorTileResult> RenderAsync(
        IReadOnlyList<VectorTileLayer> layers,
        TileCoordinate tile,
        ITileScheme scheme,
        string version,
        CancellationToken cancellationToken)
    {
        if (!scheme.IsValid(tile))
        {
            throw SpatialException.BadArguments($"Tile {tile.Z}/{tile.X}/{tile.Y} is outside the {scheme.Id} scheme.");
        }

        var key = new VectorTileCacheKey(scheme.Id, tile.Z, tile.X, tile.Y, version);
        if (await _cache.TryGetVectorAsync(key, cancellationToken).ConfigureAwait(false) is { } cached)
        {
            return new VectorTileResult(cached, true);
        }

        var tileValue = await _encoder.RenderAsync(new VectorTileRequest(scheme.Bounds(tile), scheme.Crs, layers), cancellationToken).ConfigureAwait(false);
        await _cache.SetVectorAsync(key, tileValue, cancellationToken).ConfigureAwait(false);
        return new VectorTileResult(tileValue, false);
    }

    /// <summary>
    /// The cache version for a map's vector tile (ADR-0075): the map, its
    /// layers and their styles, plus the folded content version of every
    /// dataset the tile reads, so a write invalidates exactly the tiles derived
    /// from that data.
    /// </summary>
    public static string Version(string map, IEnumerable<MapLayer> layers, string dataVersion) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            map + "\n"
            + string.Join("\n", layers.Select(layer => $"{layer.LayerId}:{layer.Dataset}:{layer.Store}:{layer.Style}"))
            + "\n" + dataVersion)));
}
