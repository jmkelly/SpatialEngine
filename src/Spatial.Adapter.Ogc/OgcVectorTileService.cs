using System.Security.Cryptography;
using System.Text;
using Spatial.Contracts;
using Spatial.Contracts.Providers;

namespace Spatial.Adapter.Ogc;

/// <summary>
/// Adapter-owned orchestration for OGC vector tiles. It deliberately shares
/// the T-002 scheme, cache and encoder contracts with the neutral and Esri
/// routes, while keeping OGC addressing and protocol failures in this
/// adapter. No MVT encoding or feature querying happens here.
/// </summary>
public sealed class OgcVectorTileService
{
    private readonly IVectorTileService _encoder;
    private readonly ITileCache _cache;
    private readonly IReadOnlyDictionary<string, ITileScheme> _schemes;
    private readonly OgcOptions _options;

    public OgcVectorTileService(
        IVectorTileService encoder,
        IEnumerable<ITileScheme> schemes,
        ITileCache cache,
        OgcOptions options)
    {
        ArgumentNullException.ThrowIfNull(schemes);
        _encoder = encoder ?? throw new ArgumentNullException(nameof(encoder));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _schemes = schemes.ToDictionary(scheme => scheme.Id, StringComparer.OrdinalIgnoreCase);
        if (_schemes.Count == 0 || !_schemes.ContainsKey(_options.DefaultTileScheme))
        {
            throw new InvalidOperationException("No configured default tile scheme is registered.");
        }
    }

    public ITileScheme Resolve(string? matrixSetId)
    {
        var requested = string.IsNullOrWhiteSpace(matrixSetId)
            ? _options.DefaultTileScheme
            : matrixSetId;
        var scheme = _schemes.Values.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, requested, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(MatrixSetId(candidate), requested, StringComparison.OrdinalIgnoreCase));
        return scheme ?? throw SpatialException.BadArguments($"Unknown OGC tile matrix set '{requested}'.");
    }

    public string MatrixSetId(ITileScheme scheme) =>
        string.Equals(scheme.Id, "webmercator", StringComparison.OrdinalIgnoreCase)
            ? _options.WebMercatorTileMatrixSet
            : scheme.Id;

    public async Task<VectorTile> RenderAsync(
        IReadOnlyList<VectorTileLayer> layers,
        Map map,
        TileCoordinate tile,
        string? matrixSetId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var scheme = Resolve(matrixSetId);
        if (!scheme.IsValid(tile))
        {
            throw SpatialException.BadArguments(
                FormattableString.Invariant($"Tile {tile.Z}/{tile.X}/{tile.Y} is outside the {MatrixSetId(scheme)} tile matrix set."));
        }

        var version = Version(map);
        var key = new VectorTileCacheKey(scheme.Id, tile.Z, tile.X, tile.Y, version);
        if (await _cache.TryGetVectorAsync(key, cancellationToken).ConfigureAwait(false) is { } cached)
        {
            return cached;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var result = await _encoder.RenderAsync(
            new VectorTileRequest(scheme.Bounds(tile), scheme.Crs, layers), cancellationToken).ConfigureAwait(false);
        await _cache.SetVectorAsync(key, result, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public static string Version(Map map) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        map.Name + "\n" + string.Join("\n", map.Layers
            .Where(layer => layer.Kind == MapLayerKind.Feature)
            .Select(layer => $"{layer.LayerId}:{layer.Dataset}:{layer.Store}:{layer.Name}")))));
}
