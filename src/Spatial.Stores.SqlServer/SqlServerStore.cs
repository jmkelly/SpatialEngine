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
/// <see cref="ITransactionStore"/> on
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
public sealed class SqlServerStore : IDataCatalogue, IFeatureStore, IFeatureAggregateStore, IFeatureLookup, ITransactionStore, IAsyncDisposable
{
    private readonly SqlServerConnectionConfiguration _configuration;
    private readonly SqlServerStorage _storage;
    private int _disposed;

    public SqlServerStore(SqlServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _configuration = string.IsNullOrWhiteSpace(options.ConnectionString)
            ? SqlServerConnectionConfiguration.FromEnvironment()
            : SqlServerConnectionConfiguration.FromConnectionString(options.ConnectionString);
        _storage = new SqlServerStorage(_configuration, options.CreateIndexes);
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

    /// <summary>The count of the features a plan selects, over the pushed-down restriction.</summary>
    public Task<int> CountAsync(string dataset, FeatureQuery query, CancellationToken cancellationToken = default) =>
        ReduceAsync(dataset, query, selected => FeatureReduction.CountFeatures(selected.Features), cancellationToken);

    /// <summary>The deduplicated field combinations a plan selects.</summary>
    public Task<DistinctPage> DistinctAsync(
        string dataset, FeatureQuery query, DistinctQuery distinct, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(distinct);
        return ReduceAsync(dataset, query, selected => FeatureReduction.Distinct(selected.Schema, selected.Features, distinct), cancellationToken);
    }

    /// <summary>The grouped reduction a plan selects.</summary>
    public Task<AggregatePage> AggregateAsync(
        string dataset, FeatureQuery query, AggregateQuery aggregate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        return ReduceAsync(dataset, query, selected => FeatureReduction.Aggregate(selected.Schema, selected.Features, aggregate), cancellationToken);
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

    private Task<TResult> ReduceAsync<TResult>(
        string dataset, FeatureQuery query, Func<(FeatureSchema Schema, List<Feature> Features), TResult> reduce, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var name = ParseDataset(dataset);
        RequireConfigured();
        ValidateBoundingBox(query.BoundingBox);
        return RunStoreOperationAsync(async () => reduce(await SelectAsync(name, query, cancellationToken)));
    }

    /// <summary>A plan with only its restriction: a reduction must see every selected row.</summary>
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
        return await Features.WriteAsync(name, description, batch, transaction, cancellationToken);
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
