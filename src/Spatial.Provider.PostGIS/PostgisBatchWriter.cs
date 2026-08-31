using Npgsql;
using Spatial.Core.Features;
using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Providers;
using Spatial.PluginSdk.Resources;
using Spatial.Provider.PostGIS.Core;
using Spatial.Provider.PostGIS.Data;

namespace Spatial.Provider.PostGIS;

/// <summary>
/// The feature.write surface (ADR-0028): appends a validated batch, enlisting
/// in an active transaction when one is supplied and otherwise running on a
/// fresh connection inside an implicit transaction it commits. Failures
/// propagate to the guarded facade.
/// </summary>
internal sealed class PostgisBatchWriter
{
    private readonly Lazy<PostgisDataStore> _store;
    private readonly PostgisSchemaReader _schemaReader;
    private readonly PostgisTransactionRegistry _transactions;

    public PostgisBatchWriter(
        Lazy<PostgisDataStore> store,
        PostgisSchemaReader schemaReader,
        PostgisTransactionRegistry transactions)
    {
        _store = store;
        _schemaReader = schemaReader;
        _transactions = transactions;
    }

    /// <summary>Appends a validated batch, enlisting in the given transaction when one is active.</summary>
    public async ValueTask<CapabilityResult> ExecuteWriteAsync(
        CapabilityInvocation invocation,
        PostgisDatasetName dataset,
        FeatureBatch batch,
        ResourceId? transactionId)
    {
        var description = await _schemaReader.DescribeAsync(invocation, dataset, invocation.CancellationToken);
        if (!description.Schema.IsDecodableFrom(batch.Schema))
        {
            return CapabilityResult.Failure(PostgisInvocationValidator.NotDecodable(invocation, dataset, description));
        }

        return CapabilityResult.Success(await AppendAsync(invocation, dataset, description, batch, transactionId));
    }

    private async Task<long> AppendAsync(
        CapabilityInvocation invocation,
        PostgisDatasetName dataset,
        DatasetDescription description,
        FeatureBatch batch,
        ResourceId? transactionId)
    {
        if (transactionId is { } id)
        {
            return await AppendEnlistedAsync(invocation, dataset, description, batch, id);
        }

        return await AppendImplicitAsync(invocation, dataset, description, batch);
    }

    /// <summary>Appends a batch inside an active transaction's connection (no implicit commit).</summary>
    private async Task<long> AppendEnlistedAsync(
        CapabilityInvocation invocation,
        PostgisDatasetName dataset,
        DatasetDescription description,
        FeatureBatch batch,
        ResourceId transactionId)
    {
        if (!_transactions.TryGet(transactionId, out var state))
        {
            throw new PostgisInactiveTransactionException("the transaction is no longer active; begin a new one and retry the write.");
        }

        return await AppendAsync(dataset, state.Connection, description.Srid, batch, invocation.CancellationToken);
    }

    /// <summary>Appends the batch on a fresh connection inside an implicit transaction it commits.</summary>
    private async Task<long> AppendImplicitAsync(
        CapabilityInvocation invocation,
        PostgisDatasetName dataset,
        DatasetDescription description,
        FeatureBatch batch)
    {
        await using var connection = await _store.Value.OpenConnectionAsync(invocation.CancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(invocation.CancellationToken);
        var appended = await AppendAsync(dataset, connection, description.Srid, batch, invocation.CancellationToken);
        await transaction.CommitAsync(invocation.CancellationToken);
        return appended;
    }

    private static async Task<long> AppendAsync(
        PostgisDatasetName dataset,
        NpgsqlConnection connection,
        int srid,
        FeatureBatch batch,
        CancellationToken token)
    {
        var sql = PostgisQueries.Insert(dataset, batch.Schema, srid);
        long appended = 0;
        foreach (var feature in batch.Features)
        {
            token.ThrowIfCancellationRequested();
            await PostgisDataStore.ExecuteNonQueryAsync(connection, sql, PostgisRowMapper.Parameters(batch.Schema, feature, srid), token);
            appended++;
        }

        return appended;
    }
}
