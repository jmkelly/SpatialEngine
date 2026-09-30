using Microsoft.Data.SqlClient;
using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Features.Query;
using Spatial.Querying;
using Spatial.Stores.SqlServer.Configuration;
using Spatial.Stores.SqlServer.Core;
using Spatial.Stores.SqlServer.Data;

namespace Spatial.Stores.SqlServer;

/// <summary>
/// The SQL Server store (ADR-0033, ADR-0073): a direct, in-process
/// implementation of <see cref="IDataCatalogue"/>, <see cref="IFeatureStore"/>,
/// <see cref="IFeatureAggregateStore"/>, <see cref="IFeatureLookup"/> and
/// <see cref="ITransactionStore"/> and <see cref="IVersionedFeatureStore"/> on
/// Microsoft.Data.SqlClient. SqlClient types, T-SQL and WKB stay inside this
/// assembly (ADR-0005). This type is the composition root of the store: it
/// validates arguments, maps failures and owns the lifecycle, while the
/// catalogue face (<see cref="SqlServerCatalogue"/>), the feature face
/// (<see cref="SqlServerFeatures"/>) and the transaction handles
/// (<see cref="SqlServerTransactions"/>) do the work. Reads return canonical
/// <see cref="FeatureBatch"/> pages (or, for the additive read-by-identity
/// face, single features, ADR-0038); writes append in one transaction;
/// transactions are string handles over open connections owned here.
/// Unconfigured (no connection string) throws <c>store.unavailable</c>; bad
/// identifiers/field names/filters throw <c>invalid.arguments</c>;
/// diagnostics are redacted (database name only, never the secret).
/// </summary>
public sealed class SqlServerStore : IDataCatalogue, IFeatureStore, IFeatureAggregateStore, IFeatureLookup, ITransactionStore, IVersionedFeatureStore, IAsyncDisposable
{
    private readonly SqlServerConnectionConfiguration _configuration;
    private readonly SqlServerStorage _storage;
    private int _disposed;

    public SqlServerStore(SqlServerOptions options)
        : this(options, TimeProvider.System)
    {
    }

    /// <summary>
    /// The same store over a clock the caller moves, so what expires by time
    /// (the description cache, ADR-0151) is testable without sleeping.
    /// </summary>
    internal SqlServerStore(SqlServerOptions options, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        _configuration = string.IsNullOrWhiteSpace(options.ConnectionString)
            ? SqlServerConnectionConfiguration.FromEnvironment()
            : SqlServerConnectionConfiguration.FromConnectionString(options.ConnectionString);
        _storage = new SqlServerStorage(_configuration, options.CreateIndexes, options.DescriptionCacheTtl, clock);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _storage.DisposeAsync();
    }

    private SqlServerCatalogue Catalogue => _storage.Catalogue;

    private SqlServerFeatures Features => _storage.Features;

    public Task<IReadOnlyList<DatasetSummary>> ListAsync(string? pattern = null, CancellationToken cancellationToken = default)
    {
        RequireConfigured();
        return RunStoreOperationAsync(() => Catalogue.ListAsync(pattern, cancellationToken));
    }

    public Task<DatasetDescription> DescribeAsync(string dataset, CancellationToken cancellationToken = default) =>
        DescribeAsync(ParseDataset(dataset), cancellationToken);

    private Task<DatasetDescription> DescribeAsync(SqlServerDatasetName name, CancellationToken cancellationToken)
    {
        RequireConfigured();
        return RunStoreOperationAsync(() => Catalogue.DescribeAsync(name, cancellationToken));
    }

    public Task<string> CreateAsync(string dataset, FeatureBatch sample, int srid, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sample);
        var name = ParseDataset(dataset);
        RequireConfigured();
        return RunStoreOperationAsync(() => Catalogue.CreateAsync(name, sample, srid, cancellationToken));
    }

    public Task<IReadOnlyList<FeatureBatch>> ScanAsync(string dataset, CancellationToken cancellationToken = default)
    {
        var name = ParseDataset(dataset);
        RequireConfigured();
        return RunStoreOperationAsync(() => Features.ScanAsync(name, cancellationToken));
    }

    /// <summary>
    /// The plan read: the restriction, the order and the page are compiled to
    /// T-SQL and read by the database, and the page is finished with the total
    /// the count statement answered (ADR-0074 §4-5, ADR-0116 §1, ADR-0124).
    ///
    /// <para>
    /// A plan whose order T-SQL cannot reproduce the reference's is finished
    /// here instead, with the shared reference executor over the rows the
    /// pushed restriction selected — the arrangement this store had before the
    /// page was pushed, and the right answer in every case; only the number of
    /// rows that crossed the wire differs. So a plan with an order the table
    /// can make total is a capped read with <c>OFFSET</c>/<c>FETCH NEXT</c>, and
    /// a plan without one is a whole read paged in process.
    /// </para>
    /// </summary>
    public async Task<FeatureQueryPage> QueryAsync(
        string dataset, FeatureQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var name = ParseDataset(dataset);
        RequireConfigured();
        ValidateBoundingBox(query.BoundingBox);
        return await RunStoreOperationAsync(async () =>
        {
            var pushed = await new SqlServerPlanReader(_storage, Catalogue).ReadAsync(name, query, cancellationToken);
            if (pushed is not null)
            {
                return pushed;
            }

            var (schema, selected) = await SelectAsync(name, query, cancellationToken);
            return FeaturePlanExecutor.Finish(schema, selected, query, cancellationToken);
        });
    }

    /// <summary>
    /// The count of the features a plan selects, counted by the database over
    /// the restriction the plan pushed (ADR-0133 §2).
    /// </summary>
    public async Task<int> CountAsync(string dataset, FeatureQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var name = ParseDataset(dataset);
        RequireConfigured();
        ValidateBoundingBox(query.BoundingBox);
        return await RunStoreOperationAsync(async () =>
            await new SqlServerPlanReader(_storage, Catalogue).CountAsync(name, query, cancellationToken));
    }

    /// <summary>
    /// The deduplicated field combinations a plan selects, as a
    /// <c>SELECT DISTINCT</c> when the plan's order is total over them, and
    /// reduced over the read — with the shared reference, so the set keeps the
    /// first-seen order the contract's is — when it is not (ADR-0133 §6).
    /// </summary>
    public async Task<DistinctPage> DistinctAsync(
        string dataset, FeatureQuery query, DistinctQuery distinct, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(distinct);
        var name = ParseDataset(dataset);
        RequireConfigured();
        ValidateBoundingBox(query.BoundingBox);
        return await RunStoreOperationAsync(async () =>
        {
            var pushed = await new SqlServerPlanReader(_storage, Catalogue).DistinctAsync(name, query, distinct, cancellationToken);
            if (pushed is not null)
            {
                return pushed;
            }

            var (schema, selected) = await SelectAsync(name, query, cancellationToken);
            return FeatureReduction.Distinct(schema, selected, distinct, query.Order);
        });
    }

    /// <summary>
    /// The grouped reduction a plan selects, as a <c>GROUP BY</c> when this
    /// dialect can return the reference's group order and answer every
    /// statistic the request names, and finished with the shared reference over
    /// the rows the restriction selected when it cannot (ADR-0133 §4).
    ///
    /// <para>
    /// The plan's order is handed to the reduction on both paths, never applied
    /// to the rows this store reads: a group order is a total order over the
    /// <em>groups</em>, and only the reduction knows which rows a group has. It
    /// matters here because the read is a reduction face's read — it arrives in
    /// whatever order T-SQL gave the rows, which under the container's collation
    /// is neither the plan's order nor the order the rows were written in, so a
    /// group sequence taken from the read is a page of an undefined order
    /// (ADR-0128 §8, the same obligation
    /// <see cref="FeaturePlanFallback.AggregateAsync"/> has).
    /// </para>
    ///
    /// <para>
    /// The pushed statement is the other half of that obligation: T-SQL writes
    /// the plan's group order as an <c>ORDER BY</c> with the contract's null
    /// placement, ahead of the <c>OFFSET</c>/<c>FETCH NEXT</c> the group page is
    /// cut with, so the groups come back in the plan's order rather than in the
    /// order the server grouped them.
    /// </para>
    /// </summary>
    public async Task<AggregatePage> AggregateAsync(
        string dataset, FeatureQuery query, AggregateQuery aggregate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(aggregate);
        var name = ParseDataset(dataset);
        RequireConfigured();
        ValidateBoundingBox(query.BoundingBox);
        return await RunStoreOperationAsync(async () =>
        {
            var pushed = await new SqlServerPlanReader(_storage, Catalogue).AggregateAsync(name, query, aggregate, cancellationToken);
            if (pushed is not null)
            {
                return pushed;
            }

            var (schema, selected) = await SelectAsync(name, query, cancellationToken);
            return FeatureReduction.Aggregate(schema, selected, aggregate, query.Order);
        });
    }

    /// <summary>
    /// Every feature a plan's <em>restriction</em> selects. The plan is
    /// validated against the dataset's schema first, so an unknown field or a
    /// negative cap is the typed <c>invalid.arguments</c> the contract promises
    /// rather than whatever T-SQL would have made of it (ADR-0074 §1). The
    /// shaping members (projection, order, cap, cursor) are stripped, because a
    /// reduction must see the whole selected set.
    /// </summary>
    /// <remarks>
    /// The restriction is pushed into T-SQL where the dataset's features can
    /// still be named afterwards, and applied here over the whole read where
    /// they cannot: a dataset with no identity column names its features by the
    /// ordinal of the read, so a <c>WHERE</c> that returned only some of the
    /// rows would renumber them — the same feature would come back with an id
    /// that depends on the query, breaking <c>objectIds</c>,
    /// <c>returnIdsOnly</c>, paging and the edit round-trip (ADR-0097). Either
    /// way this is the reference's selected set, not T-SQL's.
    /// </remarks>
    private async Task<(FeatureSchema Schema, List<Feature> Features)> SelectAsync(
        SqlServerDatasetName name, FeatureQuery query, CancellationToken cancellationToken)
    {
        var description = await Features.DescribeAsync(name, cancellationToken);
        var schema = (FeatureSchema)description.Schema;
        var plan = Restriction(query);
        FeatureQueryValidation.Validate(schema, plan);

        var restricts = plan.Ids is not null || plan.BoundingBox is not null || plan.Where is not null;
        var pushed = !restricts || description.IdColumns.Count > 0;
        var batches = pushed
            ? await Features.QueryAsync(name, plan, cancellationToken)
            : await Features.ScanAsync(name, cancellationToken);
        var rows = batches.SelectMany(batch => batch.Features).ToList();
        return (schema, pushed ? rows : FeaturePlanExecutor.Select(schema, rows, plan, cancellationToken));
    }

    private static FeatureQuery Restriction(FeatureQuery query) =>
        query with { Projection = null, Order = null, Limit = null, Offset = null, Cursor = null };

    private static void ValidateBoundingBox(BoundingBox? bbox)
    {
        if (bbox is { } box && !IsOrdered(box))
        {
            throw SpatialException.BadArguments("The bounding box requires minx <= maxx and miny <= maxy.");
        }
    }

    private static bool IsOrdered(BoundingBox box) => box.MinX <= box.MaxX && box.MinY <= box.MaxY;

    /// <inheritdoc />
    public Task<IReadOnlyList<Feature>> GetAsync(
        string dataset, IReadOnlyList<FeatureId> ids, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var name = ParseDataset(dataset);
        RequireConfigured();
        return ids.Count == 0
            ? Task.FromResult<IReadOnlyList<Feature>>([])
            : RunStoreOperationAsync(() => Features.ByIdentityAsync(name, ids, cancellationToken));
    }

    public Task<int> WriteAsync(
        string dataset,
        FeatureBatch batch,
        string? transaction = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var name = ParseDataset(dataset);
        RequireConfigured();
        return RunStoreOperationAsync(() => WriteConfiguredAsync(name, batch, transaction, cancellationToken));
    }

    private async Task<int> WriteConfiguredAsync(
        SqlServerDatasetName name,
        FeatureBatch batch,
        string? transaction,
        CancellationToken cancellationToken)
    {
        var description = await DescribeInternalAsync(name, cancellationToken);
        try
        {
            return await Features.WriteAsync(name, description, batch, transaction, cancellationToken);
        }
        finally
        {
            // The description was read before the write, so it describes the
            // dataset as it was before it; a description carries the row
            // estimate too, and an append is what moves it. A write that did
            // not complete cleanly is exactly the case where the store should
            // not claim to know (ADR-0151).
            ForgetDescription(name);
        }
    }

    /// <inheritdoc />
    public async ValueTask<string> GetContentVersionAsync(string dataset, CancellationToken cancellationToken = default)
    {
        var name = ParseDataset(dataset);
        RequireConfigured();
        return await RunStoreOperationAsync(async () =>
        {
            await using var connection = await _storage.OpenConnectionAsync(cancellationToken);
            return await SqlServerContentVersions.VersionAsync(connection, name, cancellationToken);
        });
    }

    public Task<string> BeginAsync(CancellationToken cancellationToken = default)
    {
        RequireConfigured();
        return RunStoreOperationAsync(() => _storage.Transactions.OpenAsync(cancellationToken));
    }

    public Task<bool> CommitAsync(string transaction, CancellationToken cancellationToken = default) =>
        EndAsync(transaction, commit: true, cancellationToken);

    public Task<bool> RollbackAsync(string transaction, CancellationToken cancellationToken = default) =>
        EndAsync(transaction, commit: false, cancellationToken);

    private Task<bool> EndAsync(string transaction, bool commit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        return RunStoreOperationAsync(() => _storage.Transactions.EndAsync(transaction, commit, cancellationToken));
    }

    internal Task<DatasetDescription> DescribeInternalAsync(SqlServerDatasetName name, CancellationToken token) =>
        Catalogue.DescribeAsync(name, token);

    /// <summary>
    /// Drops the description this store is holding for a dataset it has just
    /// changed (ADR-0151), so the next read discovers the dataset as it now is
    /// rather than as it was when it was last read.
    /// </summary>
    internal void ForgetDescription(SqlServerDatasetName name) => _storage.Descriptions.Invalidate(name);

    /// <summary>
    /// How many catalogue description reads this store has issued — the cost a
    /// paged walk used to pay once per page, and the claim ADR-0151 is measured
    /// by.
    /// </summary>
    internal long DescriptionReads => _storage.Descriptions.Reads;

    /// <summary>How many descriptions the store is holding right now; a write path drops its dataset's.</summary>
    internal int CachedDescriptions => _storage.Descriptions.Count;

    /// <summary>Runs a store operation, wrapping only unexpected failures as <c>store.unavailable</c>.</summary>
    internal async Task<T> RunStoreOperationAsync<T>(Func<Task<T>> operation)
    {
        try
        {
            return await operation();
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not SpatialException)
        {
            throw StoreFailure(exception);
        }
    }

    /// <summary>Opens a pooled connection for the ingest and attachment faces (ADR-0041); the caller owns the lifecycle.</summary>
    internal Task<SqlConnection> OpenIngestConnectionAsync(CancellationToken cancellationToken) =>
        _storage.OpenConnectionAsync(cancellationToken);

    /// <summary>Whether a dataset this store creates carries its indexes (ADR-0092).</summary>
    internal bool CreateIndexes => _storage.CreateIndexes;

    /// <summary>Opens the connection a store-side edit runs on: the transaction handle's connection, or a fresh autocommit one (ADR-0037).</summary>
    internal Task<SqlServerEditSession> OpenEditSessionAsync(string? transaction, CancellationToken cancellationToken) =>
        _storage.Transactions.OpenEditSessionAsync(transaction, cancellationToken);

    internal static SqlServerDatasetName ParseDataset(string dataset)
    {
        if (!SqlServerDatasetName.TryParse(dataset, out var name, out var reason))
        {
            throw SpatialException.BadArguments($"Invalid dataset identifier: {reason}");
        }

        return name;
    }

    internal void RequireConfigured()
    {
        if (!_configuration.IsConfigured)
        {
            throw SpatialException.Unavailable(
                $"No connection configuration (set Spatial:SqlServer:ConnectionString or {SqlServerOptions.EnvironmentVariable}).");
        }
    }

    internal SpatialException StoreFailure(Exception exception) =>
        exception is SpatialException spatial ? spatial
        : new SpatialException(
            SpatialException.StoreUnavailable,
            _configuration.Redact($"The SQL Server store failed: {exception.Message}"),
            exception);
}
