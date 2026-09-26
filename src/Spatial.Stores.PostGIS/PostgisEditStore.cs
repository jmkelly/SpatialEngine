using Npgsql;
using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Stores.PostGIS.Core;

namespace Spatial.Stores.PostGIS;

/// <summary>
/// The PostGIS feature-editing face (ADR-0037), split from
/// <see cref="PostgisStore"/> so the store keeps one cohesive
/// read/write/transaction responsibility and the editor owns add/update/
/// delete. It shares the store's discovered schema, connection configuration
/// and transaction handles; every statement is built in
/// <see cref="PostgisQueries"/> from discovered identifiers and bound
/// parameters, and each feature reports a <see cref="FeatureEditOutcome"/>.
/// </summary>
public sealed class PostgisEditStore : IFeatureEditStore
{
    private readonly PostgisStore _store;

    public PostgisEditStore(PostgisStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<FeatureEditOutcome>> AddAsync(
        string dataset, FeatureBatch batch, string? transaction = null, CancellationToken cancellationToken = default) =>
        EditBatchAsync(dataset, batch, transaction, update: false, PostgisEditOutcomes.ApplyInsertAsync, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<FeatureEditOutcome>> UpdateAsync(
        string dataset, FeatureBatch batch, string? transaction = null, CancellationToken cancellationToken = default) =>
        EditBatchAsync(dataset, batch, transaction, update: true, PostgisEditOutcomes.ApplyUpdateAsync, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<FeatureEditOutcome>> DeleteAsync(
        string dataset, IReadOnlyList<FeatureId> featureIds, string? transaction = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(featureIds);
        var name = PostgisStore.ParseDataset(dataset);
        _store.RequireConfigured();
        return _store.RunStoreOperationAsync(
            () => DeleteAllAsync(name, featureIds, transaction, cancellationToken));
    }

    private async Task<IReadOnlyList<FeatureEditOutcome>> DeleteAllAsync(
        PostgisDatasetName name,
        IReadOnlyList<FeatureId> featureIds,
        string? transaction,
        CancellationToken cancellationToken)
    {
        var description = await _store.DescribeInternalAsync(name, cancellationToken);
        if (description.IdColumns.Count == 0)
        {
            return WithoutPrimaryKey(featureIds.Select(id => id), "deleted");
        }

        await using var session = await _store.OpenEditSessionAsync(transaction, cancellationToken);
        return await PostgisFeatureDeletes.DeleteAsync(session, name, description, featureIds, cancellationToken);
    }

    private Task<IReadOnlyList<FeatureEditOutcome>> EditBatchAsync(
        string dataset,
        FeatureBatch batch,
        string? transaction,
        bool update,
        Func<Feature, NpgsqlCommand, CancellationToken, Task<FeatureEditOutcome>> apply,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var name = PostgisStore.ParseDataset(dataset);
        _store.RequireConfigured();
        return _store.RunStoreOperationAsync(
            () => EditAllAsync(name, batch, transaction, update, apply, cancellationToken));
    }

    private async Task<IReadOnlyList<FeatureEditOutcome>> EditAllAsync(
        PostgisDatasetName name,
        FeatureBatch batch,
        string? transaction,
        bool update,
        Func<Feature, NpgsqlCommand, CancellationToken, Task<FeatureEditOutcome>> apply,
        CancellationToken cancellationToken)
    {
        var description = await _store.DescribeInternalAsync(name, cancellationToken);
        PostgisWriteOperations.CheckWritable(description, batch);
        if (description.IdColumns.Count == 0)
        {
            return WithoutPrimaryKey(batch.Features.Select(feature => feature.Id), "edited");
        }

        await using var session = await _store.OpenEditSessionAsync(transaction, cancellationToken);
        return await EditEachAsync(session, new BatchEdit(name, description, batch, update), apply, cancellationToken);
    }

    /// <summary>One edit batch: the target dataset, its description, the features and whether they are updated.</summary>
    private sealed record BatchEdit(
        PostgisDatasetName Name,
        DatasetDescription Description,
        FeatureBatch Batch,
        bool Update);

    private static async Task<IReadOnlyList<FeatureEditOutcome>> EditEachAsync(
        PostgisEditSession session,
        BatchEdit edit,
        Func<Feature, NpgsqlCommand, CancellationToken, Task<FeatureEditOutcome>> apply,
        CancellationToken cancellationToken)
    {
        var outcomes = new List<FeatureEditOutcome>(edit.Batch.Count);
        foreach (var feature in edit.Batch.Features)
        {
            outcomes.Add(await ApplyFeatureAsync(session, edit, feature, apply, cancellationToken));
        }

        return outcomes;
    }

    /// <summary>Rejects every feature of a dataset the store discovered no primary key for.</summary>
    private static FeatureEditOutcome[] WithoutPrimaryKey(IEnumerable<FeatureId> ids, string operation) =>
        ids.Select(id => FeatureEditOutcome.Failure(
            id,
            SpatialException.InvalidArguments,
            $"The dataset has no primary key, so features cannot be {operation}."))
            .ToArray();

    private static async Task<FeatureEditOutcome> ApplyFeatureAsync(
        PostgisEditSession session,
        BatchEdit edit,
        Feature feature,
        Func<Feature, NpgsqlCommand, CancellationToken, Task<FeatureEditOutcome>> apply,
        CancellationToken cancellationToken)
    {
        try
        {
            var (sql, values) = PostgisWriteOperations.PlanFeature(edit.Name, edit.Description, feature, edit.Update);
            await using var command = session.CreateCommand(sql, values);
            return await apply(feature, command, cancellationToken);
        }
        catch (Exception exception) when (PostgisEditOutcomes.IsFeatureFailure(exception))
        {
            return PostgisEditOutcomes.FailureFor(feature.Id, exception);
        }
    }

}
