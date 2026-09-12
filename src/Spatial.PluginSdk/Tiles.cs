using Spatial.Core.Geometry;

namespace Spatial.PluginSdk;

/// <summary>
/// A tile address in a named tiling scheme: zoom <c>Z</c> and the
/// column/row <c>X</c>/<c>Y</c> within that zoom. The origin corner is the
/// scheme's own (Web-Mercator XYZ is top-left). Core-typed so it can travel
/// through contracts and cache keys (ADR-0005/ADR-0046).
/// </summary>
public readonly record struct TileCoordinate(int Z, int X, int Y);

/// <summary>
/// One level of detail of a <see cref="ITileScheme"/>: the integer zoom and
/// the ground resolution (CRS units per pixel) and scale denominator a
/// tiling client needs. The shape mirrors the Esri LOD vocabulary without
/// adopting it (ADR-0046).
/// </summary>
public sealed record TileLevel(int Zoom, double Resolution, double ScaleDenominator);

/// <summary>
/// The mapping between tile addresses and projected extents for one tiling
/// scheme (ADR-0046). Implementations own the projection and level-of-detail
/// math; the tile renderer and cache depend only on this contract, so a
/// second projection is a new implementation, never a change to the
/// renderer.
/// </summary>
public interface ITileScheme
{
    /// <summary>Stable scheme key used in requests and tile cache keys (for example <c>webmercator</c>).</summary>
    string Id { get; }

    /// <summary>The CRS the tile extents are expressed in (for example <c>EPSG:3857</c>).</summary>
    string Crs { get; }

    /// <summary>The edge length in pixels of a tile at every level.</summary>
    int TileSize { get; }

    /// <summary>The lowest supported zoom level (usually 0).</summary>
    int MinZoom { get; }

    /// <summary>The highest supported zoom level.</summary>
    int MaxZoom { get; }

    /// <summary>The levels of detail indexed by zoom, so <c>Levels[z]</c> is zoom <c>z</c>.</summary>
    IReadOnlyList<TileLevel> Levels { get; }

    /// <summary>Whether the coordinate addresses a tile this scheme can produce.</summary>
    bool IsValid(TileCoordinate coordinate);

    /// <summary>The projected extent of <paramref name="coordinate"/> in <see cref="Crs"/> units.</summary>
    Envelope Bounds(TileCoordinate coordinate);

    /// <summary>The ground resolution (CRS units per pixel) at <paramref name="zoom"/>.</summary>
    double Resolution(int zoom);
}

/// <summary>
/// The content-addressed identity of one rendered tile (ADR-0046): scheme,
/// address, encoding and a caller-computed <c>Version</c> that folds the
/// style and dataset state. Two requests with different versions never
/// collide, so a style or dataset change invalidates its tiles without
/// scanning the cache.
/// </summary>
public sealed record TileCacheKey(
    string Scheme,
    int Z,
    int X,
    int Y,
    RasterFormat Format,
    string Version);

/// <summary>
/// A rendered-tile cache. Implementations decide ownership (process memory,
/// filesystem, object store) and eviction policy (ADR-0046); callers only
/// see content-addressed get/set/remove/clear, never where the bytes live.
/// </summary>
public interface ITileCache
{
    /// <summary>The cached image for <paramref name="key"/>, or <c>null</c> on a miss.</summary>
    ValueTask<RasterImage?> TryGetAsync(TileCacheKey key, CancellationToken cancellationToken = default);

    /// <summary>Stores (or replaces) the image for <paramref name="key"/>; the implementation may evict older entries.</summary>
    ValueTask SetAsync(TileCacheKey key, RasterImage image, CancellationToken cancellationToken = default);

    /// <summary>Removes one entry, returning whether it was present.</summary>
    ValueTask<bool> RemoveAsync(TileCacheKey key, CancellationToken cancellationToken = default);

    /// <summary>Removes every entry.</summary>
    ValueTask ClearAsync(CancellationToken cancellationToken = default);
}
