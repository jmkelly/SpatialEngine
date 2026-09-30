using Npgsql;
using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Features.Query;
using Spatial.Stores.PostGIS.Configuration;
using Spatial.Stores.PostGIS.Core;
using Spatial.Stores.PostGIS.Data;

namespace Spatial.Stores.PostGIS;

/// <summary>
/// The PostGIS store (ADR-0033): a direct, in-process implementation of
/// <see cref="IDataCatalogue"/>, <see cref="IFeatureStore"/>,
/// <see cref="IFeatureAggregateStore"/>, <see cref="IFeatureLookup"/> and
/// <see cref="ITransactionStore"/> on Npgsql
/// 10. Npgsql types, SQL and EWKB stay inside this assembly (ADR-0005).
/// This type is the composition root of the store: it validates arguments,
/// maps failures and owns the lifecycle, while the catalogue face
/// (<see cref="PostgisCatalogue"/>), the feature face (<see cref="PostgisFeatures"/>)
/// and the transaction handles (<see cref="PostgisTransactions"/>) do the work.
/// Reads return canonical <see cref="FeatureBatch"/> pages (or, for the
/// additive read-by-identity face, single features, ADR-0038); writes append
/// in one transaction; transactions are string handles over open connections
/// owned here. Unconfigured (no connection string) throws
/// <c>store.unavailable</c>; bad identifiers/field names/filters throw
/// <c>invalid.arguments</c>; diagnostics are redacted (database name only,
/// never the secret).
/// </summary>
public sealed class PostgisStore : IDataCatalogue, IFeatureStore, IFeatureAggregateStore, IFeatureLookup, ITransactionStore, IAsyncDisposable
{
    private readonly PostgisConnectionConfiguration _configuration;
    private readonly PostgisStorage _storage;
    private int _disposed;

    public PostgisStore(PostgisOptions options)
        : this(options, TimeProvider.System)
    {
    }

    /// <summary>
    /// The same store over a clock the caller moves, so what expires by time
    /// (the description cache, ADR-0122) is testable without sleeping.
    /// </summary>
    internal PostgisStore(PostgisOptions options, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        _configuration = string.IsNullOrWhiteSpace(options.ConnectionString)
            ? PostgisConnectionConfiguration.FromEnvironment()
            : PostgisConnectionConfiguration.FromConnectionString(options.ConnectionString);
        _storage = new PostgisStorage(_configuration, options.CreateIndexes, options.DescriptionCacheTtl, clock);
    }

    internal PostgisStore(PostgisConnectionConfiguration configuration)
    {
        _configuration = configuration;
        _storage = new PostgisStorage(configuration);
    }

    private PostgisCatalogue Catalogue => _storage.Catalogue;

    private PostgisFeatures Features => _storage.Features;

    private PostgisPlanReader Plans => new(_storage, _storage.Catalogue);

    public Task<IReadOnlyList<DatasetSummary>> ListAsync(string? pattern = null, CancellationToken cancellationToken = default)
    {
        RequireConfigured();
        return RunStoreOperationAsync(() => Catalogue.ListAsync(pattern, cancellationToken));
    }

    public Task<DatasetDescription> DescribeAsync(string dataset, CancellationToken cancellationToken = default) =>
        DescribeAsync(ParseDataset(dataset), cancellationToken);

    private Task<DatasetDescription> DescribeAsync(PostgisDatasetName name, CancellationToken cancellationToken)
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
    /// The plan read: the whole plan is compiled to SQL — the attribute
    /// predicate and the bounding-box pre-filter as one parameterised
    /// <c>WHERE</c>, the identity restriction as an OR-group of identity
    /// tuples, the projection, the order and the row cap — and whatever the
    /// dialect cannot express is finished here with the shared reference
    /// executor, over the rows that were read (ADR-0074 §4). The answer is the
    /// reference's answer either way; only the rows that crossed the wire
    /// differ.
    /// </summary>
    public Task<FeatureQueryPage> QueryAsync(
        string dataset, FeatureQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var name = ParseDataset(dataset);
        RequireConfigured();
        ValidateBoundingBox(query.BoundingBox);
        return RunStoreOperationAsync(() => Plans.ReadAsync(name, query, cancellationToken));
    }

    /// <summary>The count of the rows a plan selects, counted by the database.</summary>
    public Task<int> CountAsync(string dataset, FeatureQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var name = ParseDataset(dataset);
        RequireConfigured();
        ValidateBoundingBox(query.BoundingBox);
        return RunStoreOperationAsync(() => Plans.CountAsync(name, query, cancellationToken));
    }

    /// <summary>The deduplicated field combinations a plan selects.</summary>
    public Task<DistinctPage> DistinctAsync(
        string dataset, FeatureQuery query, DistinctQuery distinct, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(distinct);
        var name = ParseDataset(dataset);
        RequireConfigured();
        ValidateBoundingBox(query.BoundingBox);
        return RunStoreOperationAsync(() => Plans.DistinctAsync(name, query, distinct, cancellationToken));
    }

    /// <summary>The grouped reduction a plan selects, pushed down where the dialect allows.</summary>
    public Task<AggregatePage> AggregateAsync(
        string dataset, FeatureQuery query, AggregateQuery aggregate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(aggregate);
        var name = ParseDataset(dataset);
        RequireConfigured();
        ValidateBoundingBox(query.BoundingBox);
        return RunStoreOperationAsync(() => Plans.AggregateAsync(name, query, aggregate, cancellationToken));
    }

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
        PostgisDatasetName name,
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
            // not claim to know (ADR-0122).
            ForgetDescription(name);
        }
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

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _storage.DisposeAsync();
    }

    internal Task<DatasetDescription> DescribeInternalAsync(PostgisDatasetName name, CancellationToken token) =>
        Catalogue.DescribeAsync(name, token);

    /// <summary>
    /// Whether this database already compares text by bytes, which is what
    /// decides whether a statement comparing a <em>text</em> column carries an
    /// explicit <c>COLLATE "C"</c> — a sort key (ADR-0121), a predicate
    /// (ADR-0123) or an identity comparison (ADR-0126). Read only when the
    /// dataset's identity has a text column, which is the only identity
    /// comparison a collation can change; a dataset keyed on a number asks for
    /// nothing and pays nothing.
    /// </summary>
    internal Task<bool> ByteOrderTextAsync(DatasetDescription description, CancellationToken token) =>
        PostgisIdentity.ComparesText(description)
            ? _storage.ByteOrderTextAsync(token)
            : Task.FromResult(false);

    /// <summary>
    /// Drops the description this store is holding for a dataset it has just
    /// changed (ADR-0122), so the next read discovers the dataset as it now is
    /// rather than as it was when it was last read.
    /// </summary>
    internal void ForgetDescription(PostgisDatasetName name) => _storage.Descriptions.Invalidate(name);

    /// <summary>
    /// How many catalogue description reads this store has issued — the cost a
    /// paged walk used to pay once per page, and the claim ADR-0122 is measured
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

    /// <summary>Opens a pooled connection for the ingest face (ADR-0041); the caller owns the transaction and lifecycle.</summary>
    internal Task<NpgsqlConnection> OpenIngestConnectionAsync(CancellationToken cancellationToken) =>
        _storage.OpenConnectionAsync(cancellationToken);

    /// <summary>Whether a dataset this store creates carries its indexes (ADR-0092).</summary>
    internal bool CreateIndexes => _storage.CreateIndexes;

    /// <summary>Opens the connection a store-side edit runs on: the transaction handle's connection, or a fresh autocommit one (ADR-0037).</summary>
    internal Task<PostgisEditSession> OpenEditSessionAsync(string? transaction, CancellationToken cancellationToken) =>
        _storage.Transactions.OpenEditSessionAsync(transaction, cancellationToken);

    internal static PostgisDatasetName ParseDataset(string dataset)
    {
        if (!PostgisDatasetName.TryParse(dataset, out var name, out var reason))
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
                $"No connection configuration (set Spatial:Postgis:ConnectionString or {PostgisOptions.EnvironmentVariable}).");
        }
    }

    internal SpatialException StoreFailure(Exception exception) =>
        exception is SpatialException spatial ? spatial
        : new SpatialException(
            SpatialException.StoreUnavailable,
            _configuration.Redact($"The PostGIS store failed: {exception.Message}"),
            exception);
}
