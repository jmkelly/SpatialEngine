using System.Collections.Concurrent;
using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;

namespace Spatial.Stores.Demo;

/// <summary>
/// The demo store (ADR-0033): a direct, in-process implementation of
/// <see cref="IDataCatalogue"/>, <see cref="IFeatureStore"/> and
/// <see cref="IDemoWork"/> over the procedural <see cref="DemoDatasetCatalogue"/>.
/// Read-only (writes and dataset creation throw <c>invalid.arguments</c>);
/// the demo sleep is a cancellable delay reporting progress.
/// </summary>
public sealed class DemoStore : IDataCatalogue, IFeatureStore, IDemoWork
{
    private const int BatchSize = 64;

    /// <summary>
    /// The unfiltered summaries, built once: the catalog is static and
    /// read-only (ADR-0031/ADR-0033), so the summaries are immutable and
    /// safe to share across requests. Cheap (T-095): the world-cities entry
    /// reports the committed snapshot count without parsing the CSV, so cold
    /// listings stay fast until demo.world_cities is actually requested.
    /// </summary>
    private static readonly Lazy<IReadOnlyList<DatasetSummary>> CachedSummaries = new(
        () => DemoDatasetCatalogue.Summaries.ToArray(),
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

    /// <summary>
    /// The features a query plan selects: the identity restriction, the
    /// bounding-box pre-filter and the attribute predicate, evaluated in
    /// process over the generated catalogue (ADR-0074 §2 — a store without a
    /// pushdown answers the plan itself).
    /// </summary>
    public Task<IReadOnlyList<FeatureBatch>> QueryAsync(string dataset, FeatureQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        var bbox = query.BoundingBox;
        var found = Find(dataset);
        IEnumerable<Feature> selected = found.Features;
        if (query.Ids is { Count: > 0 })
        {
            var wanted = new HashSet<FeatureId>(query.Ids);
            selected = selected.Where(feature => wanted.Contains(feature.Id));
        }

        if (bbox is not null)
        {
            selected = selected.Where(feature => Intersects(feature, found, bbox));
        }

        if (query.Where is { } where)
        {
            // Resolve the predicate's fields against the catalogue's schema
            // once, so an unknown column is a typed invalid-argument failure
            // before any feature is read.
            RequireKnownFields(found, where);
            selected = selected.Where(feature => DemoPredicate.Matches(where, feature));
        }

        var features = selected.ToArray();
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

        return DemoDatasetCatalogue.Find(dataset)
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

    /// <summary>The typed failure for a predicate over a field the dataset does not have.</summary>
    private static void RequireKnownFields(DemoDataset dataset, Core.Features.Query.Predicate where)
    {
        foreach (var field in where.Fields())
        {
            if (dataset.SchemaFields.IndexOf(field.Name) < 0)
            {
                var fields = string.Join(", ", dataset.SchemaFields.Fields.Select(known => $"'{known.Name}'"));
                throw SpatialException.BadArguments(
                    $"The filter column '{field.Name}' is not a field of this dataset; available fields: {fields}.");
            }
        }
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
