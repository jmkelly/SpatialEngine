using Npgsql;
using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Stores.PostGIS.Core;
using Spatial.Stores.PostGIS.Data;

namespace Spatial.Stores.PostGIS;

/// <summary>
/// The PostGIS ingest face (ADR-0041 §3): create a dataset from a
/// decoded upload's schema and load every page in **one transaction**, so the
/// table exists only if every feature lands. The identity mode decides whether
/// the new table gets a database-generated key (<see cref="IngestIdentity.Auto"/>),
/// a key from a named integer field (<see cref="IngestIdentity.Source"/>) or
/// no key at all (<see cref="IngestIdentity.None"/>). The plan and every
/// statement are built by <see cref="PostgisIngestPlan"/> and
/// <see cref="PostgisQueries"/> from validated identifiers and bound
/// parameters only. Additive like the other granular faces: a store that
/// cannot bulk-load simply does not implement this interface.
/// </summary>
public sealed class PostgisIngestStore : IDatasetIngest
{
    /// <summary>PostgreSQL <c>duplicate_table</c> (the dataset already exists).</summary>
    private const string DuplicateTable = "42P07";

    /// <summary>PostgreSQL <c>duplicate_object</c> (the primary-key constraint already exists).</summary>
    private const string DuplicateObject = "42710";

    private readonly PostgisStore _store;

    public PostgisIngestStore(PostgisStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <inheritdoc />
    public async Task<IngestOutcome> IngestAsync(
        IngestRequest request, IReadOnlyList<FeatureBatch> pages, CancellationToken cancellationToken = default)
    {
        var plan = PostgisIngestPlan.Create(request, pages);
        _store.RequireConfigured();
        try
        {
            await LoadAsync(plan, cancellationToken);
            return new IngestOutcome(plan.Dataset.Qualified, plan.FeatureCount, plan.Srid, plan.IdentityColumn);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PostgresException exception) when (exception.SqlState is DuplicateTable or DuplicateObject)
        {
            throw SpatialException.BadArguments($"Dataset '{plan.Dataset}' already exists.");
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

    /// <summary>Runs the whole create + load on one connection and one transaction.</summary>
    private async Task LoadAsync(PostgisIngestPlan plan, CancellationToken cancellationToken)
    {
        await using var connection = await _store.OpenIngestConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await PostgisDataStore.ExecuteNonQueryAsync(connection, transaction, plan.CreateTableSql(), [], cancellationToken);
        var insert = PostgisQueries.Insert(plan.Dataset, plan.Schema, plan.Srid);
        foreach (var page in plan.Pages)
        {
            foreach (var feature in page.Features)
            {
                var values = PostgisRowMapper.Parameters(plan.Schema, feature, plan.Srid);
                await using var command = PostgisDataStore.BuildCommand(connection, insert, values);
                command.Transaction = transaction;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }
}
