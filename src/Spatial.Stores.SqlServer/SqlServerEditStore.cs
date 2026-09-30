using Microsoft.Data.SqlClient;
using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Stores.SqlServer.Core;

namespace Spatial.Stores.SqlServer;

/// <summary>
/// The SQL Server feature-editing face (ADR-0037), split from
/// <see cref="SqlServerStore"/> so the store keeps one cohesive
/// read/write/transaction responsibility and the editor owns add/update/
/// delete. It shares the store's discovered schema, connection configuration
/// and transaction handles; every statement is built in
/// <see cref="SqlServerQueries"/> from discovered identifiers and bound
/// parameters, and each feature reports a <see cref="FeatureEditOutcome"/>.
/// </summary>
public sealed class SqlServerEditStore : IFeatureEditStore
{
    private readonly SqlServerStore _store;

    public SqlServerEditStore(SqlServerStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<FeatureEditOutcome>> AddAsync(
        string dataset, FeatureBatch batch, string? transaction = null, CancellationToken cancellationToken = default) =>
        EditBatchAsync(dataset, batch, transaction, update: false, SqlServerEditOutcomes.ApplyInsertAsync, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<FeatureEditOutcome>> UpdateAsync(
        string dataset, FeatureBatch batch, string? transaction = null, CancellationToken cancellationToken = default) =>
        EditBatchAsync(dataset, batch, transaction, update: true, SqlServerEditOutcomes.ApplyUpdateAsync, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<FeatureEditOutcome>> DeleteAsync(
        string dataset, IReadOnlyList<FeatureId> featureIds, string? transaction = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(featureIds);
        var name = SqlServerStore.ParseDataset(dataset);
        _store.RequireConfigured();
        return _store.RunStoreOperationAsync(
            () => DeleteAllAsync(name, featureIds, transaction, cancellationToken));
    }

    private async Task<IReadOnlyList<FeatureEditOutcome>> DeleteAllAsync(
        SqlServerDatasetName name,
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
        var outcomes = await SqlServerFeatureDeletes.DeleteAsync(session, name, description, featureIds, cancellationToken);
        await BumpWhenChangedAsync(session, name, outcomes, cancellationToken);
        return outcomes;
    }

    private Task<IReadOnlyList<FeatureEditOutcome>> EditBatchAsync(
        string dataset,
        FeatureBatch batch,
        string? transaction,
        bool update,
        Func<Feature, SqlCommand, CancellationToken, Task<FeatureEditOutcome>> apply,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var name = SqlServerStore.ParseDataset(dataset);
        _store.RequireConfigured();
        return _store.RunStoreOperationAsync(
            () => EditAllAsync(name, batch, transaction, update, apply, cancellationToken));
    }

    private async Task<IReadOnlyList<FeatureEditOutcome>> EditAllAsync(
        SqlServerDatasetName name,
        FeatureBatch batch,
        string? transaction,
        bool update,
        Func<Feature, SqlCommand, CancellationToken, Task<FeatureEditOutcome>> apply,
        CancellationToken cancellationToken)
    {
        var description = await _store.DescribeInternalAsync(name, cancellationToken);
        SqlServerWriteOperations.CheckWritable(description, batch);
        if (description.IdColumns.Count == 0)
        {
            return WithoutPrimaryKey(batch.Features.Select(feature => feature.Id), "edited");
        }

        await using var session = await _store.OpenEditSessionAsync(transaction, cancellationToken);
        var outcomes = await EditEachAsync(session, new BatchEdit(name, description, batch, update), apply, cancellationToken);
        await BumpWhenChangedAsync(session, name, outcomes, cancellationToken);
        return outcomes;
    }

    /// <summary>
    /// Moves the dataset's content version when the batch changed at least one
    /// feature (ADR-0129): a partially applied batch still changed something,
    /// a batch where every feature failed changed nothing and leaves the cached
    /// tiles valid. The bump runs on the session's own connection and
    /// transaction, so inside a transaction handle a rollback restores the
    /// features and the version together.
    /// </summary>
    private static Task BumpWhenChangedAsync(
        SqlServerEditSession session,
        SqlServerDatasetName name,
        IReadOnlyList<FeatureEditOutcome> outcomes,
        CancellationToken cancellationToken) =>
        outcomes.Any(outcome => outcome.Succeeded)
            ? SqlServerContentVersions.BumpAsync(session.Connection, session.Transaction, name, cancellationToken)
            : Task.CompletedTask;

    /// <summary>One edit batch: the target dataset, its description, the features and whether they are updated.</summary>
    private sealed record BatchEdit(
        SqlServerDatasetName Name,
        DatasetDescription Description,
        FeatureBatch Batch,
        bool Update);

    private static async Task<IReadOnlyList<FeatureEditOutcome>> EditEachAsync(
        SqlServerEditSession session,
        BatchEdit edit,
        Func<Feature, SqlCommand, CancellationToken, Task<FeatureEditOutcome>> apply,
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
        SqlServerEditSession session,
        BatchEdit edit,
        Feature feature,
        Func<Feature, SqlCommand, CancellationToken, Task<FeatureEditOutcome>> apply,
        CancellationToken cancellationToken)
    {
        try
        {
            var (sql, values) = SqlServerWriteOperations.PlanFeature(edit.Name, edit.Description, feature, edit.Update);
            await using var command = session.CreateCommand(sql, values);
            return await apply(feature, command, cancellationToken);
        }
        catch (Exception exception) when (SqlServerEditOutcomes.IsFeatureFailure(exception))
        {
            return SqlServerEditOutcomes.FailureFor(feature.Id, exception);
        }
    }

}
