using System.Collections.Concurrent;
using Spatial.Core.Features;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Providers;

namespace Spatial.Provider.Demo;

/// <summary>
/// The demo store (ADR-0033): a direct, in-process implementation of
/// <see cref="IDataCatalogue"/>, <see cref="IFeatureStore"/> and
/// <see cref="IDemoJobs"/> over the procedural <see cref="DemoDatasetCatalog"/>.
/// Read-only (writes and dataset creation throw <c>invalid.arguments</c>);
/// the sleep job is a cancellable delay reporting progress.
/// </summary>
public sealed class DemoStore : IDataCatalogue, IFeatureStore, IDemoJobs
{
    private const int BatchSize = 64;

    /// <summary>
    /// The unfiltered summaries, built once: the catalog is static and
    /// read-only (ADR-0031/ADR-0033), so the summaries are immutable and
    /// safe to share across requests. Lazy so first-load timing (including
    /// the world-cities snapshot parse) matches the uncached path.
    /// </summary>
    private static readonly Lazy<IReadOnlyList<DatasetSummary>> CachedSummaries = new(
        () => DemoDatasetCatalog.Datasets.Select(dataset => dataset.ToSummary()).ToArray(),
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// One description per dataset, built once: descriptions derive
    /// entirely from the immutable catalog entries (identity, row count,
    /// shared schema), and consumers only read them.
    /// </summary>
    private static readonly ConcurrentDictionary<string, DatasetDescription> CachedDescriptions = new(StringComparer.Ordinal);

    public Task<IReadOnlyList<DatasetSummary>> ListAsync(string? pattern = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (pattern is null)
        {
            return Task.FromResult(CachedSummaries.Value);
        }

        IReadOnlyList<DatasetSummary> result = CachedSummaries.Value
            .Where(summary => LikePattern.Matches(summary.Id, pattern))
            .ToArray();
        return Task.FromResult(result);
    }

    public Task<DatasetDescription> DescribeAsync(string dataset, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(dataset))
        {
            throw SpatialException.BadArguments("A dataset identifier is required.");
        }

        return Task.FromResult(CachedDescriptions.GetOrAdd(dataset, static id => Find(id).ToDescription()));
    }

    public Task<string> CreateAsync(string dataset, FeatureBatch sample, int srid, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sample);
        throw SpatialException.BadArguments("The demo store is read-only; dataset creation is not supported.");
    }

    public Task<IReadOnlyList<FeatureBatch>> ScanAsync(string dataset, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var found = Find(dataset);
        IReadOnlyList<FeatureBatch> scanned = Page(found.Features, found.SchemaFields);
        return Task.FromResult(scanned);
    }

    public Task<IReadOnlyList<FeatureBatch>> QueryAsync(string dataset, BoundingBox? bbox = null, string? filter = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (filter is not null)
        {
            throw SpatialException.BadArguments("The demo store supports bbox queries only; attribute filters are not supported.");
        }

        var found = Find(dataset);
        var features = bbox is null
            ? found.Features
            : found.Features.Where(feature => Intersects(feature, found, bbox)).ToArray();
        IReadOnlyList<FeatureBatch> paged = Page(features, found.SchemaFields);
        return Task.FromResult(paged);
    }

    public Task<int> WriteAsync(string dataset, FeatureBatch batch, string? transaction = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        throw SpatialException.BadArguments("The demo store is read-only; writes are not supported.");
    }

    public async Task<long> SleepAsync(long milliseconds, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (milliseconds < 0)
        {
            throw SpatialException.BadArguments($"'milliseconds' must be non-negative, got {milliseconds}.");
        }

        const int Steps = 10;
        var step = milliseconds / Steps;
        var remainder = milliseconds % Steps;
        for (var i = 0; i < Steps; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromMilliseconds(step + (i == 0 ? remainder : 0)), cancellationToken);
            progress?.Report((i + 1) / (double)Steps);
        }

        return milliseconds;
    }

    private static DemoDataset Find(string dataset)
    {
        if (string.IsNullOrWhiteSpace(dataset))
        {
            throw SpatialException.BadArguments("A dataset identifier is required.");
        }

        return DemoDatasetCatalog.Find(dataset)
            ?? throw SpatialException.Missing($"Unknown dataset '{dataset}'.");
    }

    private static List<FeatureBatch> Page(IReadOnlyList<Feature> features, FeatureSchema schema)
    {
        if (features.Count == 0)
        {
            return [new FeatureBatch(schema, [])];
        }

        var batches = new List<FeatureBatch>();
        for (var i = 0; i < features.Count; i += BatchSize)
        {
            batches.Add(new FeatureBatch(schema, features.Skip(i).Take(BatchSize).ToArray()));
        }

        return batches;
    }

    private static bool Intersects(Feature feature, DemoDataset dataset, BoundingBox bbox)
    {
        var box = dataset.BoxFor(feature.Id);
        if (box is null)
        {
            return false;
        }

        var value = box.Value;
        return value.MinX <= bbox.MaxX && value.MaxX >= bbox.MinX
            && value.MinY <= bbox.MaxY && value.MaxY >= bbox.MinY;
    }
}
