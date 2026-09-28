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
/// <see cref="IDatasetIngestStream"/> is the same load with the pages arriving
/// as they are decoded, so the upload is never held in memory.
/// </summary>
public sealed class PostgisIngestStore : IDatasetIngest, IDatasetIngestStream
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
    public Task<IngestOutcome> IngestAsync(
        IngestRequest request, IReadOnlyList<FeatureBatch> pages, CancellationToken cancellationToken = default)
    {
        var plan = PostgisIngestPlan.Create(request, pages);
        _store.RequireConfigured();
        return _store.RunStoreOperationAsync(async () =>
        {
            try
            {
                await LoadAsync(plan, cancellationToken).ConfigureAwait(false);
            }
            catch (PostgresException exception) when (IsAlreadyCreated(exception))
            {
                throw AlreadyExists(plan.Dataset);
            }

            return new IngestOutcome(plan.Dataset.Qualified, plan.FeatureCount, plan.Srid, plan.IdentityColumn);
        });
    }

    private static bool IsAlreadyCreated(PostgresException exception) =>
        exception.SqlState is DuplicateTable or DuplicateObject;

    private static SpatialException AlreadyExists(PostgisDatasetName dataset) =>
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

        var plan = PostgisIngestPlan.Create(request, schema);
        _store.RequireConfigured();
        return await _store.RunStoreOperationAsync(async () =>
        {
            try
            {
                var loaded = await LoadStreamAsync(plan, pages, cancellationToken).ConfigureAwait(false);
                return new IngestOutcome(plan.Dataset.Qualified, loaded, plan.Srid, plan.IdentityColumn);
            }
            catch (PostgresException exception) when (IsAlreadyCreated(exception))
            {
                throw AlreadyExists(plan.Dataset);
            }
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates the table from the first page's geometry types and inserts each
    /// page as it arrives, so a 500 MB upload costs one page of memory rather
    /// than 500 MB. Everything stays on one connection and one transaction: a
    /// failure, a cancellation or a malformed page part-way through rolls the
    /// table back rather than leaving a half-populated dataset.
    /// </summary>
    private async Task<long> LoadStreamAsync(
        PostgisIngestPlan plan, IAsyncEnumerable<FeatureBatch> pages, CancellationToken cancellationToken)
    {
        await using var connection = await _store.OpenIngestConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        long loaded = 0;
        var position = 0;
        PostgisIngestPlan? bound = null;
        var insert = string.Empty;
        await foreach (var page in pages.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (bound is null)
            {
                bound = plan.Bind(page);
                await PostgisDataStore
                    .ExecuteNonQueryAsync(connection, transaction, bound.CreateTableSql(), [], cancellationToken)
                    .ConfigureAwait(false);
                insert = PostgisQueries.Insert(bound.Dataset, bound.Schema, bound.Srid);
            }
            else
            {
                bound.CheckPage(page, position);
            }

            await LoadPageAsync(connection, transaction, insert, bound, page, cancellationToken).ConfigureAwait(false);
            loaded += page.Count;
            position++;
        }

        if (bound is null)
        {
            throw SpatialException.BadArguments("An ingest requires at least one feature.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return loaded;
    }

    /// <summary>Runs the whole create + load on one connection and one transaction.</summary>
    private async Task LoadAsync(PostgisIngestPlan plan, CancellationToken cancellationToken)
    {
        await using var connection = await _store.OpenIngestConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await PostgisDataStore.ExecuteNonQueryAsync(connection, transaction, plan.CreateTableSql(), [], cancellationToken);
        await CreateIndexesAsync(connection, transaction, plan, cancellationToken);
        var insert = PostgisQueries.Insert(plan.Dataset, plan.Schema, plan.Srid);
        foreach (var page in plan.Pages)
        {
            await LoadPageAsync(connection, transaction, insert, plan, page, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Creates the dataset's indexes (ADR-0092) inside the load's own
    /// transaction, unless index creation is switched off. An index that cannot
    /// be created rolls the whole ingest back: the dataset does not exist
    /// rather than exists unindexed.
    /// </summary>
    private async Task CreateIndexesAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, PostgisIngestPlan plan, CancellationToken cancellationToken)
    {
        foreach (var statement in _store.CreateIndexes ? plan.CreateIndexSql() : [])
        {
            await PostgisDataStore.ExecuteNonQueryAsync(connection, transaction, statement, [], cancellationToken);
        }
    }

    private static async Task LoadPageAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string insert,
        PostgisIngestPlan plan, FeatureBatch page, CancellationToken cancellationToken)
    {
        foreach (var feature in page.Features)
        {
            var values = PostgisRowMapper.Parameters(plan.Schema, feature, plan.Srid);
            await using var command = PostgisDataStore.BuildCommand(connection, insert, values);
            command.Transaction = transaction;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
