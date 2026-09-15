using Npgsql;
using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Stores.PostGIS.Core;
using Spatial.Stores.PostGIS.Data;

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
        EditBatchAsync(dataset, batch, transaction, update: false, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<FeatureEditOutcome>> UpdateAsync(
        string dataset, FeatureBatch batch, string? transaction = null, CancellationToken cancellationToken = default) =>
        EditBatchAsync(dataset, batch, transaction, update: true, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<FeatureEditOutcome>> DeleteAsync(
        string dataset, IReadOnlyList<FeatureId> featureIds, string? transaction = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(featureIds);
        var name = PostgisStore.ParseDataset(dataset);
        _store.RequireConfigured();
        try
        {
            var description = await _store.DescribeInternalAsync(name, cancellationToken);
            if (description.IdColumns.Count == 0)
            {
                return featureIds
                    .Select(id => FeatureEditOutcome.Failure(
                        id, SpatialException.InvalidArguments, "The dataset has no primary key, so features cannot be deleted."))
                    .ToArray();
            }

            await using var session = await _store.OpenEditSessionAsync(transaction, cancellationToken);
            var outcomes = new List<FeatureEditOutcome>(featureIds.Count);
            foreach (var id in featureIds)
            {
                outcomes.Add(await DeleteFeatureAsync(session, name, description, id, cancellationToken));
            }

            return outcomes;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SpatialException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw _store.StoreFailure(exception);
        }
    }

    private async Task<IReadOnlyList<FeatureEditOutcome>> EditBatchAsync(
        string dataset, FeatureBatch batch, string? transaction, bool update, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var name = PostgisStore.ParseDataset(dataset);
        _store.RequireConfigured();
        try
        {
            var description = await _store.DescribeInternalAsync(name, cancellationToken);
            PostgisWriteOperations.CheckWritable(description, batch);
            if (description.IdColumns.Count == 0)
            {
                return batch.Features
                    .Select(feature => FeatureEditOutcome.Failure(
                        feature.Id, SpatialException.InvalidArguments, "The dataset has no primary key, so features cannot be edited."))
                    .ToArray();
            }

            await using var session = await _store.OpenEditSessionAsync(transaction, cancellationToken);
            var outcomes = new List<FeatureEditOutcome>(batch.Count);
            foreach (var feature in batch.Features)
            {
                outcomes.Add(await ApplyFeatureAsync(session, name, description, feature, update, cancellationToken));
            }

            return outcomes;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SpatialException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw _store.StoreFailure(exception);
        }
    }

    private static async Task<FeatureEditOutcome> ApplyFeatureAsync(
        PostgisEditSession session, PostgisDatasetName name, DatasetDescription description, Feature feature, bool update, CancellationToken cancellationToken)
    {
        try
        {
            var (sql, values) = PostgisWriteOperations.PlanFeature(name, description, feature, update);
            await using var command = session.Connection.CreateCommand();
            command.Transaction = session.Transaction;
            command.CommandText = sql;
            for (var i = 0; i < values.Length; i++)
            {
                command.Parameters.AddWithValue($"p{i}", values[i] ?? DBNull.Value);
            }

            if (update)
            {
                var affected = await command.ExecuteNonQueryAsync(cancellationToken);
                return affected > 0
                    ? FeatureEditOutcome.Success(feature.Id)
                    : FeatureEditOutcome.Failure(feature.Id, SpatialException.NotFound, $"No feature with identity '{feature.Id}' exists.");
            }

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken) || reader.FieldCount == 0)
            {
                return FeatureEditOutcome.Success(feature.Id);
            }

            var row = new object[reader.FieldCount];
            reader.GetValues(row);
            var indexes = Enumerable.Range(0, reader.FieldCount).ToArray();
            return FeatureEditOutcome.Success(new FeatureId(PostgisDiagnostics.FeatureIdentity(indexes, row, 0)));
        }
        catch (PostgresException exception)
        {
            return FeatureEditOutcome.Failure(feature.Id, SpatialException.InvalidArguments, exception.MessageText);
        }
        catch (SpatialException exception)
        {
            return FeatureEditOutcome.Failure(feature.Id, exception.Code, exception.Message);
        }
    }

    private static async Task<FeatureEditOutcome> DeleteFeatureAsync(
        PostgisEditSession session, PostgisDatasetName name, DatasetDescription description, FeatureId id, CancellationToken cancellationToken)
    {
        try
        {
            var kinds = description.IdColumns
                .Select(column => description.Schema[description.Schema.IndexOf(column)].Kind)
                .ToArray();
            var values = PostgisDiagnostics.ParseFeatureIdentity(kinds, id);
            await using var command = session.Connection.CreateCommand();
            command.Transaction = session.Transaction;
            command.CommandText = PostgisQueries.Delete(name, description.IdColumns);
            for (var i = 0; i < values.Length; i++)
            {
                command.Parameters.AddWithValue($"p{i}", values[i] ?? DBNull.Value);
            }

            var affected = await command.ExecuteNonQueryAsync(cancellationToken);
            return affected > 0
                ? FeatureEditOutcome.Success(id)
                : FeatureEditOutcome.Failure(id, SpatialException.NotFound, $"No feature with identity '{id}' exists.");
        }
        catch (PostgresException exception)
        {
            return FeatureEditOutcome.Failure(id, SpatialException.InvalidArguments, exception.MessageText);
        }
        catch (SpatialException exception)
        {
            return FeatureEditOutcome.Failure(id, exception.Code, exception.Message);
        }
    }
}
