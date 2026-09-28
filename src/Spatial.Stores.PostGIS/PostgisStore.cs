using Npgsql;
using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Stores.PostGIS.Configuration;
using Spatial.Stores.PostGIS.Core;
using Spatial.Stores.PostGIS.Data;

namespace Spatial.Stores.PostGIS;

/// <summary>
/// The PostGIS store (ADR-0033): a direct, in-process implementation of
/// <see cref="IDataCatalogue"/>, <see cref="IFeatureStore"/>,
/// <see cref="IFeatureLookup"/> and <see cref="ITransactionStore"/> on Npgsql
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
public sealed class PostgisStore : IDataCatalogue, IFeatureStore, IFeatureLookup, ITransactionStore, IAsyncDisposable
{
    private readonly PostgisConnectionConfiguration _configuration;
    private readonly PostgisStorage _storage;
    private int _disposed;

    public PostgisStore(PostgisOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _configuration = string.IsNullOrWhiteSpace(options.ConnectionString)
            ? PostgisConnectionConfiguration.FromEnvironment()
            : PostgisConnectionConfiguration.FromConnectionString(options.ConnectionString);
        _storage = new PostgisStorage(_configuration);
    }

    internal PostgisStore(PostgisConnectionConfiguration configuration)
    {
        _configuration = configuration;
        _storage = new PostgisStorage(configuration);
    }

    private PostgisCatalogue Catalogue => _storage.Catalogue;

    private PostgisFeatures Features => _storage.Features;

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

    public Task<IReadOnlyList<FeatureBatch>> QueryAsync(
        string dataset, FeatureQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var name = ParseDataset(dataset);
        RequireConfigured();
        ValidateBoundingBox(query.BoundingBox);
        return RunStoreOperationAsync(() => Features.QueryAsync(name, query, cancellationToken));
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
