using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Features.Query;
using Spatial.Querying;

namespace Spatial.Stores.Memory;

/// <summary>
/// The ephemeral writable in-memory provider (ADR-0042), keyed <c>memory</c>.
/// It implements the read/write/transaction faces over
/// <see cref="MemoryCatalogue"/>; the editing and ingest faces are the sibling
/// <see cref="MemoryEditor"/> and <see cref="MemoryIngest"/> so each type
/// keeps one cohesive responsibility (the same factoring as PostGIS,
/// ADR-0040). State is <em>non-durable</em>: a process restart loses every
/// dataset. Diagnostics state that; it is a development-and-CI provider, not
/// a persistence guarantee.
/// </summary>
public sealed class MemoryStore : IDataCatalogue, IFeatureStore, IFeatureLookup, IFeatureAggregateStore, ITransactionStore
{
    private const int BatchSize = 512;

    private readonly MemoryCatalogue _catalog = new();

    /// <summary>Whether the store durably persists datasets (always false, ADR-0042).</summary>
    public static bool Durable => false;

    internal MemoryCatalogue Catalog => _catalog;

    internal T WithLock<T>(Func<T> action) => _catalog.WithLock(action);

    public Task<IReadOnlyList<DatasetSummary>> ListAsync(string? pattern = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_catalog.WithLock(() => _catalog.List(pattern)));
    }

    public Task<DatasetDescription> DescribeAsync(string dataset, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_catalog.WithLock(() => _catalog.Find(dataset).ToDescription()));
    }

    public Task<string> CreateAsync(string dataset, FeatureBatch sample, int srid, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sample);
        cancellationToken.ThrowIfCancellationRequested();
        if (!MemoryDataset.IsValidId(dataset))
        {
            throw SpatialException.BadArguments($"Invalid dataset identifier '{dataset}': expected schema.table.");
        }

        if (srid <= 0)
        {
            throw SpatialException.BadArguments($"The SRID must be positive, got {srid}.");
        }

        return Task.FromResult(_catalog.WithLock(() =>
        {
            if (_catalog.Contains(dataset))
            {
                throw SpatialException.BadArguments($"Dataset '{dataset}' already exists.");
            }

            var stored = MemoryDataset.Create(dataset, srid, sample.Schema, [], [], nextId: 1);
            _catalog.Add(stored);
            return stored.Id;
        }));
    }

    public Task<IReadOnlyList<FeatureBatch>> ScanAsync(string dataset, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_catalog.WithLock<IReadOnlyList<FeatureBatch>>(() =>
        {
            var found = _catalog.Find(dataset);
            return Page(found, found.Features);
        }));
    }

    /// <summary>
    /// The plan read: the reference executor over the dataset, so the
    /// in-memory provider defines the plan's semantics rather than
    /// approximating them (ADR-0074 §4). It is also the fallback every
    /// pushdown is measured against, which is why the executor is shared
    /// rather than copied here.
    /// </summary>
    public Task<FeatureQueryPage> QueryAsync(
        string dataset, FeatureQuery query, CancellationToken cancellationToken = default) =>
        FeaturePlanFallback.ReadAsync(this, dataset, query, cancellationToken);

    /// <summary>
    /// The count, distinct and aggregate faces, over the reference executor.
    /// The in-memory provider has nothing to push down, so implementing the
    /// optional face is not slower than not implementing it: the answer is the
    /// same and the caller does not have to know which stores reduce.
    /// </summary>
    public Task<int> CountAsync(string dataset, FeatureQuery query, CancellationToken cancellationToken = default) =>
        FeaturePlanFallback.CountAsync(this, dataset, query, cancellationToken);

    /// <inheritdoc cref="CountAsync"/>
    public Task<DistinctPage> DistinctAsync(
        string dataset, FeatureQuery query, DistinctQuery distinct, CancellationToken cancellationToken = default) =>
        FeaturePlanFallback.DistinctAsync(this, dataset, query, distinct, cancellationToken);

    /// <inheritdoc cref="CountAsync"/>
    public Task<AggregatePage> AggregateAsync(
        string dataset, FeatureQuery query, AggregateQuery aggregate, CancellationToken cancellationToken = default) =>
        FeaturePlanFallback.AggregateAsync(this, dataset, query, aggregate, cancellationToken);

    /// <inheritdoc cref="CountAsync"/>
    public Task<IReadOnlyList<FeatureBatch>> QueryFilterAsync(
        string dataset, BoundingBox? bbox = null, string? filter = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (filter is not null)
        {
            throw SpatialException.BadArguments("The in-memory store supports bbox queries only; attribute filters are not supported.");
        }

        return Task.FromResult(_catalog.WithLock<IReadOnlyList<FeatureBatch>>(() =>
        {
            var found = _catalog.Find(dataset);
            var features = bbox is null
                ? found.Features
                : found.Features.Where(feature => Intersects(found, feature, bbox)).ToList();
            return Page(found, features);
        }));
    }

    public Task<int> WriteAsync(string dataset, FeatureBatch batch, string? transaction = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_catalog.WithLock(() =>
        {
            var found = _catalog.Find(dataset);
            if (transaction is not null && !_catalog.IsTransaction(transaction))
            {
                throw SpatialException.BadArguments($"Unknown transaction '{transaction}'.");
            }

            foreach (var feature in batch.Features)
            {
                found.Features.Add(MemorySchema.BuildStored(found, feature, assignedId: null));
            }

            return batch.Count;
        }));
    }

    public Task<IReadOnlyList<Feature>> GetAsync(
        string dataset, IReadOnlyList<FeatureId> ids, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_catalog.WithLock<IReadOnlyList<Feature>>(() =>
        {
            var found = _catalog.Find(dataset);
            var wanted = new HashSet<FeatureId>(ids);
            return found.Features.Where(feature => wanted.Contains(feature.Id)).ToArray();
        }));
    }

    public Task<string> BeginAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_catalog.WithLock(_catalog.Begin));
    }

    public Task<bool> CommitAsync(string transaction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_catalog.WithLock(() => _catalog.Commit(transaction)));
    }

    public Task<bool> RollbackAsync(string transaction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_catalog.WithLock(() => _catalog.Rollback(transaction)));
    }

    private static List<FeatureBatch> Page(MemoryDataset dataset, List<Feature> features)
    {
        if (features.Count == 0)
        {
            return [new FeatureBatch(dataset.Schema, [])];
        }

        var batches = new List<FeatureBatch>();
        for (var i = 0; i < features.Count; i += BatchSize)
        {
            batches.Add(new FeatureBatch(dataset.Schema, features.Skip(i).Take(BatchSize).ToArray()));
        }

        return batches;
    }

    private static bool Intersects(MemoryDataset dataset, Feature feature, BoundingBox bbox)
    {
        var index = dataset.Schema.IndexOf(dataset.GeometryColumn);
        if (index < 0 || feature[index].Kind != AttributeKind.Geometry || feature[index].GeometryValue.Envelope is not { } envelope)
        {
            return false;
        }

        return envelope.MinX <= bbox.MaxX && envelope.MaxX >= bbox.MinX
            && envelope.MinY <= bbox.MaxY && envelope.MaxY >= bbox.MinY;
    }
}
