namespace Spatial.Host.Tiling;

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
/// Bounds of the in-memory tile cache (<c>Spatial:Tiles:Cache</c>): a total
/// byte budget and an entry count. A non-positive value disables caching
/// (ADR-0046).
/// </summary>
internal sealed class TileCacheOptions
{
    public long MaxBytes { get; set; } = 64L * 1024 * 1024;

    public int MaxEntries { get; set; } = 4096;
}
