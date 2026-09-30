using Microsoft.Data.SqlClient;
using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Stores.SqlServer.Core;
using Spatial.Stores.SqlServer.Data;

namespace Spatial.Stores.SqlServer;

/// <summary>
/// The SQL Server ingest face (ADR-0041 §3 as the SQL Server provider follows
/// it): create a dataset from a decoded upload's schema and load every page in
/// **one transaction**, so the table exists only if every feature lands. The
/// identity mode decides whether the new table gets a database-generated key
/// (<see cref="IngestIdentity.Auto"/>) or a key from a named integer field
/// (<see cref="IngestIdentity.Source"/>); there is no keyless mode
/// (ADR-0149). The plan and every statement are built
/// by <see cref="SqlServerIngestPlan"/> and <see cref="SqlServerQueries"/> from
/// validated identifiers and bound parameters only. Additive like the other
/// granular faces: a store that cannot bulk-load simply does not implement
/// this interface. <see cref="IDatasetIngestStream"/> is the same load with the
/// pages arriving as they are decoded, so the upload is never held in memory.
/// </summary>
public sealed class SqlServerIngestStore : IDatasetIngest, IDatasetIngestStream
{
    private readonly SqlServerStore _store;

    public SqlServerIngestStore(SqlServerStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <inheritdoc />
    public Task<IngestOutcome> IngestAsync(
        IngestRequest request, IReadOnlyList<FeatureBatch> pages, CancellationToken cancellationToken = default)
    {
        var plan = SqlServerIngestPlan.Create(request, pages);
        _store.RequireConfigured();
        return _store.RunStoreOperationAsync(async () =>
        {
            try
            {
                await LoadAsync(plan, cancellationToken).ConfigureAwait(false);
            }
            catch (SqlException exception) when (SqlServerFailureCode.IsAlreadyCreated(exception))
            {
                throw AlreadyExists(plan.Dataset);
            }

            return new IngestOutcome(plan.Dataset.Qualified, plan.FeatureCount, plan.Srid, plan.IdentityColumn);
        });
    }

    private static SpatialException AlreadyExists(SqlServerDatasetName dataset) =>
        SpatialException.BadArguments($"Dataset '{dataset}' already exists.");

    /// <inheritdoc />
    public async Task<IngestOutcome> IngestStreamAsync(
        IngestRequest request,
        FeatureSchema schema,
        IAsyncEnumerable<FeatureBatch> pages,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(pages);

        var plan = SqlServerIngestPlan.Create(request, schema);
        _store.RequireConfigured();
        return await _store.RunStoreOperationAsync(async () =>
        {
            try
            {
                var loaded = await LoadStreamAsync(plan, pages, cancellationToken).ConfigureAwait(false);
                return new IngestOutcome(plan.Dataset.Qualified, loaded, plan.Srid, plan.IdentityColumn);
            }
            catch (SqlException exception) when (SqlServerFailureCode.IsAlreadyCreated(exception))
            {
                throw AlreadyExists(plan.Dataset);
            }
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates the table from the first page and inserts each page as it
    /// arrives, on one connection and one transaction: a failure or a
    /// cancellation part-way through rolls the table back rather than leaving
    /// a half-populated dataset.
    /// </summary>
    private async Task<long> LoadStreamAsync(
        SqlServerIngestPlan plan, IAsyncEnumerable<FeatureBatch> pages, CancellationToken cancellationToken)
    {
        await using var connection = await _store.OpenIngestConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        long loaded = 0;
        var position = 0;
        var started = false;
        var insert = string.Empty;
        await foreach (var page in pages.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (started is false)
            {
                plan.Bind(page);
                await SqlServerDataStore
                    .ExecuteNonQueryAsync(connection, transaction, plan.CreateTableSql(), [], cancellationToken)
                    .ConfigureAwait(false);
                await SqlServerCatalogue
                    .RecordSridAsync(connection, transaction, plan.Dataset, plan.Srid, cancellationToken)
                    .ConfigureAwait(false);
                insert = SqlServerQueries.Insert(plan.Dataset, plan.Schema, plan.Srid);
                started = true;
            }
            else
            {
                plan.CheckPage(page, position);
            }

            await LoadPageAsync(connection, transaction, insert, plan, page, cancellationToken).ConfigureAwait(false);
            loaded += page.Count;
            position++;
        }

        if (started is false)
        {
            throw SpatialException.BadArguments("An ingest requires at least one feature.");
        }

        // Inside the load's own transaction, so a load that rolls back leaves
        // the version where it was (ADR-0129).
        await SqlServerContentVersions.BumpAsync(connection, transaction, plan.Dataset, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return loaded;
    }

    /// <summary>Runs the whole create + load on one connection and one transaction.</summary>
    private async Task LoadAsync(SqlServerIngestPlan plan, CancellationToken cancellationToken)
    {
        await using var connection = await _store.OpenIngestConnectionAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await SqlServerDataStore.ExecuteNonQueryAsync(connection, transaction, plan.CreateTableSql(), [], cancellationToken);
        await CreateIndexesAsync(connection, transaction, plan, cancellationToken);
        await SqlServerCatalogue.RecordSridAsync(connection, transaction, plan.Dataset, plan.Srid, cancellationToken);
        var insert = SqlServerQueries.Insert(plan.Dataset, plan.Schema, plan.Srid);
        foreach (var page in plan.Pages)
        {
            await LoadPageAsync(connection, transaction, insert, plan, page, cancellationToken);
        }

        // Inside the load's own transaction, so a load that rolls back leaves
        // the version where it was (ADR-0129).
        await SqlServerContentVersions.BumpAsync(connection, transaction, plan.Dataset, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Creates the dataset's indexes (ADR-0092) inside the load's own
    /// transaction, unless index creation is switched off. An index that cannot
    /// be created rolls the whole ingest back: the dataset does not exist
    /// rather than exists unindexed.
    /// </summary>
    private async Task CreateIndexesAsync(
        SqlConnection connection, SqlTransaction transaction, SqlServerIngestPlan plan, CancellationToken cancellationToken)
    {
        foreach (var statement in _store.CreateIndexes ? plan.CreateIndexSql() : [])
        {
            await SqlServerDataStore.ExecuteNonQueryAsync(connection, transaction, statement, [], cancellationToken);
        }
    }

    private static async Task LoadPageAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string insert,
        SqlServerIngestPlan plan,
        FeatureBatch page,
        CancellationToken cancellationToken)
    {
        foreach (var feature in page.Features)
        {
            var values = SqlServerRowMapper.Parameters(plan.Schema, feature, plan.Srid);
            await using var command = SqlServerDataStore.BuildCommand(connection, insert, values);
            command.Transaction = transaction;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
