using Spatial.Core.Features;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Providers;

namespace Spatial.Provider.Memory;

/// <summary>
/// The in-memory feature-editing face (ADR-0037, ADR-0042), split from
/// <see cref="MemoryStore"/> exactly like <c>PostgisEditStore</c> so each type
/// keeps one cohesive responsibility. A feature whose identity is
/// <see cref="FeatureId.Unassigned"/> gets the dataset's next generated
/// identity (ADR-0043); a data-only dataset rejects every edit per feature,
/// mirroring the PostGIS contract.
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

            var assigned = dataset.NextId++;
            var stored = MemorySchema.BuildStored(dataset, feature, assigned);
            dataset.Features.Add(stored);
            return FeatureEditOutcome.Success(stored.Id);
        }

        var built = MemorySchema.BuildStored(dataset, feature, assignedId: null);
        dataset.Features.Add(built);
        return FeatureEditOutcome.Success(built.Id);
    }

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
}
