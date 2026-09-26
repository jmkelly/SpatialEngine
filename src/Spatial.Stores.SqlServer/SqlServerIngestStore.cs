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
/// (<see cref="IngestIdentity.Auto"/>), a key from a named integer field
/// (<see cref="IngestIdentity.Source"/>) or no key at all
/// (<see cref="IngestIdentity.None"/>). The plan and every statement are built
/// by <see cref="SqlServerIngestPlan"/> and <see cref="SqlServerQueries"/> from
/// validated identifiers and bound parameters only. Additive like the other
/// granular faces: a store that cannot bulk-load simply does not implement
/// this interface.
/// </summary>
public sealed class SqlServerIngestStore : IDatasetIngest
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

    /// <summary>Runs the whole create + load on one connection and one transaction.</summary>
    private async Task LoadAsync(SqlServerIngestPlan plan, CancellationToken cancellationToken)
    {
        await using var connection = await _store.OpenIngestConnectionAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await SqlServerDataStore.ExecuteNonQueryAsync(connection, transaction, plan.CreateTableSql(), [], cancellationToken);
        await SqlServerCatalogue.RecordSridAsync(connection, transaction, plan.Dataset, plan.Srid, cancellationToken);
        var insert = SqlServerQueries.Insert(plan.Dataset, plan.Schema, plan.Srid);
        foreach (var page in plan.Pages)
        {
            await LoadPageAsync(connection, transaction, insert, plan, page, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
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
