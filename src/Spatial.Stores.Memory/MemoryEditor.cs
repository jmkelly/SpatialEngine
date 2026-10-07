using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;

namespace Spatial.Stores.Memory;

/// <summary>
/// The in-memory feature-editing face (ADR-0037, ADR-0042), split from
/// <see cref="MemoryStore"/> exactly like <c>PostgisEditStore</c> so each type
/// keeps one cohesive responsibility. A feature whose identity is
/// <see cref="FeatureId.Unassigned"/> gets the dataset's next free generated
/// identity (ADR-0043); a data-only dataset rejects every edit per feature,
/// mirroring the PostGIS contract. An add whose identity the dataset already
/// holds is a primary-key collision, reported per feature as the keyed stores
/// do. Every edit that changes the dataset moves
/// its content version (ADR-0083), which is what invalidates the tiles derived
/// from it.
/// </summary>
public sealed class MemoryEditor : IFeatureEditStore
{
    private readonly MemoryStore _store;

    public MemoryEditor(MemoryStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<FeatureEditOutcome>> AddAsync(
        string dataset, FeatureBatch batch, string? transaction = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_store.WithLock<IReadOnlyList<FeatureEditOutcome>>(() =>
        {
            var found = _store.Catalog.Find(dataset);
            CheckTransaction(transaction);
            if (!found.Editable)
            {
                return NotEditable(found, batch.Features.Select(feature => feature.Id));
            }

            MemorySchema.CheckWritable(found, batch);
            var outcomes = new List<FeatureEditOutcome>(batch.Count);
            foreach (var feature in batch.Features)
            {
                outcomes.Add(AddOne(found, feature));
            }

            BumpWhenMutated(found, outcomes);
            return outcomes;
        }));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<FeatureEditOutcome>> UpdateAsync(
        string dataset, FeatureBatch batch, string? transaction = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_store.WithLock<IReadOnlyList<FeatureEditOutcome>>(() =>
        {
            var found = _store.Catalog.Find(dataset);
            CheckTransaction(transaction);
            if (!found.Editable)
            {
                return NotEditable(found, batch.Features.Select(feature => feature.Id));
            }

            MemorySchema.CheckWritable(found, batch);
            var outcomes = new List<FeatureEditOutcome>(batch.Count);
            foreach (var feature in batch.Features)
            {
                outcomes.Add(Replace(found, feature));
            }

            BumpWhenMutated(found, outcomes);
            return outcomes;
        }));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<FeatureEditOutcome>> DeleteAsync(
        string dataset, IReadOnlyList<FeatureId> featureIds, string? transaction = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(featureIds);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_store.WithLock<IReadOnlyList<FeatureEditOutcome>>(() =>
        {
            var found = _store.Catalog.Find(dataset);
            CheckTransaction(transaction);
            if (!found.Editable)
            {
                return NotEditable(found, featureIds);
            }

            var outcomes = new List<FeatureEditOutcome>(featureIds.Count);
            foreach (var id in featureIds)
            {
                var removed = found.Features.RemoveAll(candidate => candidate.Id.Equals(id));
                outcomes.Add(removed > 0
                    ? FeatureEditOutcome.Success(id)
                    : FeatureEditOutcome.Failure(id, SpatialException.NotFound, $"No feature with identity '{id}' exists."));
            }

            BumpWhenMutated(found, outcomes);
            return outcomes;
        }));
    }

    private static FeatureEditOutcome AddOne(MemoryDataset dataset, Feature feature)
    {
        if (feature.Id.Equals(FeatureId.Unassigned))
        {
            if (!dataset.IdColumns.Contains(MemorySchema.AutoIdentityColumn, StringComparer.Ordinal))
            {
                return FeatureEditOutcome.Failure(
                    feature.Id, SpatialException.InvalidArguments, "Only an auto-identity dataset can assign a missing identity.");
            }

            // The store owns this identity (ADR-0043), so it picks one the
            // dataset does not already hold: a value an explicit or source
            // identity took is skipped rather than stored twice under one
            // key (ADR-0038).
            Feature stored;
            do
            {
                stored = MemorySchema.BuildStored(dataset, feature, dataset.NextId++);
            }
            while (IdentityExists(dataset, stored.Id));

            dataset.Features.Add(stored);
            return FeatureEditOutcome.Success(stored.Id);
        }

        var built = MemorySchema.BuildStored(dataset, feature, assignedId: null);
        if (IdentityExists(dataset, built.Id))
        {
            return Duplicate(dataset, built.Id);
        }

        dataset.Features.Add(built);
        return FeatureEditOutcome.Success(built.Id);
    }

    /// <summary>
    /// Whether the dataset already holds a feature under this identity. A
    /// store keys its features by the identity column (ADR-0038, ADR-0112),
    /// so an add of a value already stored is the primary-key collision
    /// PostGIS and SQL Server report per feature — not a second row under one
    /// key, which a read-by-identity would then return twice.
    /// </summary>
    private static bool IdentityExists(MemoryDataset dataset, FeatureId id) =>
        dataset.Features.Any(candidate => candidate.Id.Equals(id));

    /// <summary>The add's identity is already a feature of the dataset: a per-feature invalid.arguments, as the keyed stores report it.</summary>
    private static FeatureEditOutcome Duplicate(MemoryDataset dataset, FeatureId id) =>
        FeatureEditOutcome.Failure(
            id, SpatialException.InvalidArguments,
            $"A feature with identity '{id}' already exists in dataset '{dataset.Id}'.");

    private static FeatureEditOutcome Replace(MemoryDataset dataset, Feature feature)
    {
        var index = dataset.Features.FindIndex(candidate => candidate.Id.Equals(feature.Id));
        if (index < 0)
        {
            return FeatureEditOutcome.Failure(feature.Id, SpatialException.NotFound, $"No feature with identity '{feature.Id}' exists.");
        }

        dataset.Features[index] = MemorySchema.BuildStored(dataset, feature, assignedId: null);
        return FeatureEditOutcome.Success(feature.Id);
    }

    private static FeatureEditOutcome[] NotEditable(MemoryDataset dataset, IEnumerable<FeatureId> ids) =>
        ids.Select(id => FeatureEditOutcome.Failure(
            id, SpatialException.InvalidArguments,
            $"Dataset '{dataset.Id}' has no identity column, so features cannot be edited."))
        .ToArray();

    private void CheckTransaction(string? transaction)
    {
        if (transaction is not null && !_store.Catalog.IsTransaction(transaction))
        {
            throw SpatialException.BadArguments($"Unknown transaction '{transaction}'.");
        }
    }

    /// <summary>
    /// Moves the dataset's content version when at least one feature in the
    /// batch actually changed (ADR-0083), so a derived cache keyed by the
    /// version invalidates the dataset for a partially successful edit and
    /// stays valid when every feature failed.
    /// </summary>
    private void BumpWhenMutated(MemoryDataset dataset, IReadOnlyList<FeatureEditOutcome> outcomes)
    {
        if (outcomes.Any(outcome => outcome.Succeeded))
        {
            _store.Catalog.Bump(dataset.Id);
        }
    }
}
