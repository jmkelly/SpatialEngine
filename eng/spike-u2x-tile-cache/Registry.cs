using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Stores.Memory;

namespace Spatial.Spike.TileCache;

/// <summary>
/// The single keyed store the spike publishes into, so the same
/// <see cref="IStoreRegistry"/> the host composes is the one the version fold
/// (ADR-0083) and the renderer read. Everything is registered under one store
/// key because a published MapServer reads every selected layer from the
/// service's one store, which is exactly the shape
/// <c>MapRenderEngine.DataVersionAsync</c> folds.
/// </summary>
internal sealed class SingleRegistry : IStoreRegistry
{
    private readonly IFeatureStore _features;
    private readonly IDataCatalogue _catalogue;
    private readonly IFeatureEditStore? _edits;
    private readonly ITransactionStore? _transactions;

    public SingleRegistry(
        IFeatureStore features,
        IDataCatalogue catalogue,
        IFeatureEditStore? edits = null,
        ITransactionStore? transactions = null)
    {
        _features = features;
        _catalogue = catalogue;
        _edits = edits;
        _transactions = transactions;
    }

    /// <summary>The keyed store the publication lives in.</summary>
    public string Key { get; init; } = "memory";

    public IDataCatalogue Catalogue(string store) => Same(store, _catalogue);

    public IFeatureStore Features(string store) => Same(store, _features);

    public IFeatureEditStore? EditStore(string store) => _edits is null ? null : Same(store, _edits);

    public IFeatureAttachmentStore? AttachmentStore(string store) => null;

    public ITransactionStore? Transactions(string store) => _transactions is null ? null : Same(store, _transactions);

    public IDatasetIngest? Ingest(string store) => null;

    public IRasterCatalogue? RasterCatalogue(string store) => null;

    private T Same<T>(string store, T value) =>
        string.Equals(store, Key, StringComparison.Ordinal)
            ? value
            : throw SpatialException.BadArguments($"Unknown store '{store}'.");
}

/// <summary>The spike's tile cache, instrumented so the fan-out is counted.</summary>
internal interface ICountingTileCache : ITileCache
{
    /// <summary>Live raster entries, so the invalidation fan-out is counted and not estimated.</summary>
    int Entries { get; }

    /// <summary>Lookups that found a stored tile since the last <see cref="ResetCounters"/>.</summary>
    long Hits { get; }

    /// <summary>Lookups that found nothing since the last <see cref="ResetCounters"/>.</summary>
    long Misses { get; }

    /// <summary>Bytes held by the live entries.</summary>
    long Bytes { get; }

    /// <summary>Clears the hit/miss counters, keeping the stored entries.</summary>
    void ResetCounters();
}
