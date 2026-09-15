namespace Spatial.Host.TileServing;

/// <summary>
/// Host configuration for tiles (<c>Spatial:Tiles</c>), ADR-0046: the default
/// tiling scheme, the batch cap and bounded parallelism, and the initial
/// in-memory cache bounds.
/// </summary>
internal sealed class TileOptions
{
    /// <summary>Scheme id used when a request does not name one.</summary>
    public string DefaultScheme { get; set; } = "webmercator";

    /// <summary>Largest tile list a single batch may carry.</summary>
    public int MaxTilesPerBatch { get; set; } = 64;

    /// <summary>Batch parallelism; <c>0</c> (the default) uses the processor count.</summary>
    public int Concurrency { get; set; }

    /// <summary>Bounds of the initial in-memory cache.</summary>
    public TileCacheOptions Cache { get; set; } = new();
}

/// <summary>
/// Bounds of the tile cache (<c>Spatial:Tiles:Cache</c>): which
/// <see cref="ITileCache"/> implementation owns the tiles and its bounds.
/// <c>Provider</c> <c>memory</c> (the default) keeps the initial in-memory LRU;
/// <c>file</c> persists tiles under <c>Root</c> so they survive a host restart
/// and are shared between hosts pointing at the same directory (T-001,
/// ADR-0046). A total byte budget and an entry count bound either provider;
/// a non-positive value disables caching (ADR-0046).
/// </summary>
internal sealed class TileCacheOptions
{
    public string Provider { get; set; } = "memory";

    /// <summary>
    /// The directory the <c>file</c> provider persists tiles under. Required
    /// when <see cref="Provider"/> is <c>file</c>; ignored by <c>memory</c>.
    /// </summary>
    public string Root { get; set; } = string.Empty;

    public long MaxBytes { get; set; } = 64L * 1024 * 1024;

    public int MaxEntries { get; set; } = 4096;
}
