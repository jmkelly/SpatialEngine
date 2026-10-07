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
/// ADR-0040). It also reports the per-dataset content version
/// (<see cref="IVersionedFeatureStore"/>, ADR-0083) that a derived cache such
/// as the tile cache keys on. State is <em>non-durable</em>: a process restart loses every
/// dataset. Diagnostics state that; it is a development-and-CI provider, not
/// a persistence guarantee.
/// </summary>
public sealed class MemoryStore
    : IDataCatalogue, IFeatureStore, IFeatureLookup, IFeatureAggregateStore, ITransactionStore, IVersionedFeatureStore
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
    /// in-memory provider defines the plan's semantics — the identity
    /// restriction, the attribute predicate, the bounding-box pre-filter, the
    /// order and the page — rather than approximating them (ADR-0074 §4). It is
    /// also the fallback every pushdown is measured against, which is why the
    /// executor is shared rather than copied here.
    /// </summary>
    public Task<FeatureQueryPage> QueryAsync(
        string dataset, FeatureQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_catalog.WithLock<FeatureQueryPage>(() =>
        {
            var found = _catalog.Find(dataset);
            return FeaturePlanExecutor.Execute(found.Schema, found.Features, query, cancellationToken);
        }));
    }

    /// <summary>
    /// The count, distinct and aggregate faces, over the reference executor.
    /// The in-memory provider has nothing to push down, so implementing the
    /// optional face is not slower than not implementing it: the answer is the
    /// same and the caller does not have to know which stores reduce.
    /// </summary>
    public Task<int> CountAsync(string dataset, FeatureQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_catalog.WithLock(() =>
        {
            var found = _catalog.Find(dataset);
            return FeatureReduction.CountFeatures(
                FeaturePlanExecutor.Select(found.Schema, found.Features, query, cancellationToken));
        }));
    }

    /// <inheritdoc cref="CountAsync"/>
    public Task<DistinctPage> DistinctAsync(
        string dataset, FeatureQuery query, DistinctQuery distinct, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(distinct);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_catalog.WithLock(() =>
        {
            var found = _catalog.Find(dataset);
            return FeatureReduction.Distinct(
                found.Schema, FeaturePlanExecutor.Select(found.Schema, found.Features, query, cancellationToken), distinct, query.Order);
        }));
    }

    /// <inheritdoc cref="CountAsync"/>
    public Task<AggregatePage> AggregateAsync(
        string dataset, FeatureQuery query, AggregateQuery aggregate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(aggregate);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_catalog.WithLock(() =>
        {
            var found = _catalog.Find(dataset);
            return FeatureReduction.Aggregate(
                found.Schema,
                FeaturePlanExecutor.Select(found.Schema, found.Features, query, cancellationToken),
                aggregate,
                query.Order);
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

            var stored = BuildStored(found, batch);
            foreach (var feature in stored)
            {
                found.Features.Add(feature);
            }

            if (batch.Count > 0)
            {
                _catalog.Bump(found.Id);
            }

            return batch.Count;
        }));
    }

    /// <summary>
    /// Builds the batch's stored features, refusing an identity the dataset
    /// already holds before anything is appended, so a colliding write leaves
    /// the dataset as it was — the keyed stores write the batch in one
    /// transaction and roll it back (ADR-0038, ADR-0112). A dataset that
    /// declares no identity column has no key for a <see cref="FeatureId"/> to
    /// collide with (ADR-0140, ADR-0147), so the check is the keyed one only.
    /// </summary>
    private static List<Feature> BuildStored(MemoryDataset dataset, FeatureBatch batch)
    {
        var built = new List<Feature>(batch.Count);
        if (dataset.IdColumns.Count == 0)
        {
            foreach (var feature in batch.Features)
            {
                built.Add(MemorySchema.BuildStored(dataset, feature, assignedId: null));
            }

            return built;
        }

        // A store keys its features by the identity column (ADR-0038,
        // ADR-0112), so a bulk write of an identity the dataset already holds
        // is the primary-key collision the keyed stores raise, not a second
        // row under one key. The set also catches a batch that repeats an
        // identity within itself.
        var held = dataset.Features.Select(feature => feature.Id).ToHashSet();
        foreach (var feature in batch.Features)
        {
            var candidate = MemorySchema.BuildStored(dataset, feature, assignedId: null);
            if (!held.Add(candidate.Id))
            {
                throw SpatialException.BadArguments(
                    $"A feature with identity '{candidate.Id}' already exists in dataset '{dataset.Id}'.");
            }

            built.Add(candidate);
        }

        return built;
    }

    /// <summary>
    /// The dataset's content version (ADR-0083): a counter the write, edit and
    /// ingest paths bump, so a derived cache keyed by it (the tile cache) misses
    /// for a dataset that changed and hits for one that did not. Unknown
    /// datasets report <see cref="ContentVersions.Unversioned"/> — the version is
    /// an opaque token, and a missing dataset still fails at read time.
    /// </summary>
    public ValueTask<string> GetContentVersionAsync(string dataset, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_catalog.WithLock(() => _catalog.Version(dataset)));
    }

    /// <summary>
    /// The features the dataset's identity column names. A dataset that
    /// declares none is refused rather than answered: its stored features
    /// carry whatever identity the caller or the decoder gave them — the read
    /// order of a no-identity ingest, whose numbers any write moves — and
    /// answering from that would be answering about a feature that may not be
    /// the one asked for (ADR-0140).
    /// </summary>
    public Task<IReadOnlyList<Feature>> GetAsync(
        string dataset, IReadOnlyList<FeatureId> ids, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_catalog.WithLock<IReadOnlyList<Feature>>(() =>
        {
            var found = _catalog.Find(dataset);
            if (found.IdColumns.Count == 0)
            {
                throw SpatialException.BadArguments(
                    $"Cannot read features by identity from dataset '{dataset}': it declares no identity column.");
            }

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

}
