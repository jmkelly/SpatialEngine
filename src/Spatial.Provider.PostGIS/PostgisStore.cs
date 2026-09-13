using System.Collections.Concurrent;
using Npgsql;
using Spatial.Core.Features;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Providers;
using Spatial.Provider.PostGIS.Configuration;
using Spatial.Provider.PostGIS.Core;
using Spatial.Provider.PostGIS.Data;
using CoreBoundingBox = Spatial.PluginSdk.BoundingBox;

namespace Spatial.Provider.PostGIS;

/// <summary>
/// The PostGIS store (ADR-0033): a direct, in-process implementation of
/// <see cref="IDataCatalogue"/>, <see cref="IFeatureStore"/>,
/// <see cref="IFeatureLookup"/> and <see cref="ITransactionStore"/> on Npgsql
/// 10. Npgsql types, SQL and EWKB stay inside this assembly (ADR-0005).
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
    private const int BatchSize = 512;

    private readonly PostgisConnectionConfiguration _configuration;
    private readonly Lazy<PostgisDataStore> _store;
    private readonly ConcurrentDictionary<string, TransactionEntry> _transactions = new();
    private int _disposed;

    public PostgisStore(PostgisOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _configuration = string.IsNullOrWhiteSpace(options.ConnectionString)
            ? PostgisConnectionConfiguration.FromEnvironment()
            : PostgisConnectionConfiguration.FromConnectionString(options.ConnectionString);
        _store = new Lazy<PostgisDataStore>(() => PostgisDataStore.Open(_configuration));
    }

    internal PostgisStore(PostgisConnectionConfiguration configuration)
    {
        _configuration = configuration;
        _store = new Lazy<PostgisDataStore>(() => PostgisDataStore.Open(_configuration));
    }

    public async Task<IReadOnlyList<DatasetSummary>> ListAsync(string? pattern = null, CancellationToken cancellationToken = default)
    {
        RequireConfigured();
        try
        {
            await using var connection = await _store.Value.OpenConnectionAsync(cancellationToken);
            var parameters = pattern is null ? [] : new List<object?> { pattern };
            var rows = await PostgisDataStore.ReadRowsAsync(connection, PostgisQueries.Catalogue(pattern), parameters, cancellationToken);
            return rows.Select(PostgisSchemaDiscovery.SummaryFromRow).ToArray();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw StoreFailure(exception);
        }
    }

    public async Task<DatasetDescription> DescribeAsync(string dataset, CancellationToken cancellationToken = default)
    {
        var name = ParseDataset(dataset);
        RequireConfigured();
        try
        {
            return await DescribeInternalAsync(name, cancellationToken);
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
            throw StoreFailure(exception);
        }
    }

    public async Task<string> CreateAsync(string dataset, FeatureBatch sample, int srid, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sample);
        var name = ParseDataset(dataset);
        PostgisFieldName.RequireValid(name, sample.Schema);
        if (!PostgisGeometryType.TryResolve(sample.Schema, sample.Features, out var geometryTypes, out var geometryError))
        {
            throw SpatialException.BadArguments($"The dataset '{name}' cannot be created: {geometryError}");
        }

        RequireConfigured();
        try
        {
            await using var connection = await _store.Value.OpenConnectionAsync(cancellationToken);
            await PostgisDataStore.ExecuteNonQueryAsync(
                connection, PostgisQueries.CreateTable(name, sample.Schema, srid, geometryTypes), [], cancellationToken);
            return name.Qualified;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw StoreFailure(exception);
        }
    }

    public async Task<IReadOnlyList<FeatureBatch>> ScanAsync(string dataset, CancellationToken cancellationToken = default)
    {
        var name = ParseDataset(dataset);
        RequireConfigured();
        try
        {
            var description = await DescribeInternalAsync(name, cancellationToken);
            return await ReadBatchesAsync(
                PostgisQueries.Select(name, description.Schema), [], description, cancellationToken);
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
            throw StoreFailure(exception);
        }
    }

    public async Task<IReadOnlyList<FeatureBatch>> QueryAsync(string dataset, CoreBoundingBox? bbox = null, string? filter = null, CancellationToken cancellationToken = default)
    {
        var name = ParseDataset(dataset);
        RequireConfigured();
        if (bbox is not null && (bbox.MinX > bbox.MaxX || bbox.MinY > bbox.MaxY))
        {
            throw SpatialException.BadArguments("The bounding box requires minx <= maxx and miny <= maxy.");
        }

        try
        {
            var description = await DescribeInternalAsync(name, cancellationToken);
            var parameters = new List<object?>();
            var predicate = PostgisPredicate.Build(description, bbox, filter, parameters);
            return await ReadBatchesAsync(
                PostgisQueries.Query(name, description.Schema, predicate), parameters, description, cancellationToken);
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
            throw StoreFailure(exception);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Feature>> GetAsync(
        string dataset, IReadOnlyList<FeatureId> ids, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var name = ParseDataset(dataset);
        RequireConfigured();
        if (ids.Count == 0)
        {
            return [];
        }

        try
        {
            var description = await DescribeInternalAsync(name, cancellationToken);
            if (description.IdColumns.Count == 0)
            {
                return [];
            }

            var identityKinds = description.IdColumns
                .Select(column => description.Schema[description.Schema.IndexOf(column)].Kind)
                .ToArray();
            var parameters = new List<object?>(ids.Count * identityKinds.Length);
            foreach (var id in ids)
            {
                parameters.AddRange(PostgisDiagnostics.ParseFeatureIdentity(identityKinds, id));
            }

            var batches = await ReadBatchesAsync(
                PostgisQueries.SelectByIdentity(name, description.Schema, description.IdColumns, ids.Count),
                parameters,
                description,
                cancellationToken);
            return batches.SelectMany(batch => batch.Features).ToArray();
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
            throw StoreFailure(exception);
        }
    }

    public async Task<int> WriteAsync(string dataset, FeatureBatch batch, string? transaction = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var name = ParseDataset(dataset);
        RequireConfigured();
        try
        {
            var description = await DescribeInternalAsync(name, cancellationToken);
            PostgisWriteOperations.CheckWritable(description, batch);
            if (_transactions.TryGetValue(transaction ?? string.Empty, out var entry))
            {
                return await PostgisWriteOperations.WriteOnAsync(entry.Connection, entry.Transaction, name, description, batch, cancellationToken);
            }

            if (transaction is not null)
            {
                throw SpatialException.BadArguments($"Unknown transaction '{transaction}'.");
            }

            await using var connection = await _store.Value.OpenConnectionAsync(cancellationToken);
            await using var txn = await connection.BeginTransactionAsync(cancellationToken);
            var count = await PostgisWriteOperations.WriteOnAsync(connection, txn, name, description, batch, cancellationToken);
            await txn.CommitAsync(cancellationToken);
            return count;
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
            throw StoreFailure(exception);
        }
    }

    public async Task<string> BeginAsync(CancellationToken cancellationToken = default)
    {
        RequireConfigured();
        try
        {
            var connection = await _store.Value.OpenConnectionAsync(cancellationToken);
            var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var handle = Guid.NewGuid().ToString("N");
            _transactions[handle] = new TransactionEntry(connection, transaction);
            return handle;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw StoreFailure(exception);
        }
    }

    public Task<bool> CommitAsync(string transaction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        return EndAsync(transaction, commit: true, cancellationToken);
    }

    public Task<bool> RollbackAsync(string transaction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        return EndAsync(transaction, commit: false, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var entry in _transactions.Values)
        {
            await entry.DisposeAsync();
        }

        _transactions.Clear();
        if (_store.IsValueCreated)
        {
            await _store.Value.DisposeAsync();
        }
    }

    private async Task<bool> EndAsync(string transaction, bool commit, CancellationToken cancellationToken)
    {
        if (!_transactions.TryRemove(transaction, out var entry))
        {
            throw SpatialException.BadArguments($"Unknown transaction '{transaction}'.");
        }

        try
        {
            if (commit)
            {
                await entry.Transaction.CommitAsync(cancellationToken);
            }
            else
            {
                await entry.Transaction.RollbackAsync(cancellationToken);
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw StoreFailure(exception);
        }
        finally
        {
            await entry.DisposeAsync();
        }
    }

    internal async Task<DatasetDescription> DescribeInternalAsync(PostgisDatasetName name, CancellationToken token)
    {
        await using var connection = await _store.Value.OpenConnectionAsync(token);
        var parameters = new List<object?> { name.Schema, name.Table };
        var columns = await PostgisDataStore.ReadRowsAsync(connection, PostgisQueries.ColumnsMetadata(), parameters, token);
        var geometries = await PostgisDataStore.ReadRowsAsync(connection, PostgisQueries.GeometryColumnsMetadata(), parameters, token);
        var keys = await PostgisDataStore.ReadRowsAsync(connection, PostgisQueries.PrimaryKeyColumns(), parameters, token);
        var estimateRows = await PostgisDataStore.ReadRowsAsync(connection, PostgisQueries.RowEstimate(), parameters, token);
        var facts = new PostgisSchemaDiscovery.SchemaFacts(
            columns.Select(row => new PostgisSchemaDiscovery.ColumnRow(
                (string)row[0]!, (string)row[1]!, string.Equals((string)row[2]!, "YES", StringComparison.Ordinal), (int)row[3]!)).ToArray(),
            geometries.Select(row => new PostgisSchemaDiscovery.GeometryRow((string)row[0]!, (int)row[1]!, (string)row[2]!)).ToArray(),
            keys.Select(row => (string)row[0]!).ToArray(),
            estimateRows.Count > 0 ? Convert.ToInt64(estimateRows[0][0], System.Globalization.CultureInfo.InvariantCulture) : 0);
        if (PostgisSchemaDiscovery.TryBuild(name, facts, out var description, out var reason))
        {
            return description;
        }

        throw SpatialException.Missing($"Cannot read dataset '{name}': {reason}");
    }

    private async Task<IReadOnlyList<FeatureBatch>> ReadBatchesAsync(
        string sql, IReadOnlyList<object?> parameters, DatasetDescription description, CancellationToken token)
    {
        await using var connection = await _store.Value.OpenConnectionAsync(token);
        await using var reader = await PostgisDataStore.ExecuteReaderAsync(connection, sql, parameters, token);
        var identity = IdentityIndexes(description);
        var batches = new List<FeatureBatch>();
        var features = new List<Feature>(BatchSize);
        long ordinal = 0;
        while (await reader.ReadAsync(token))
        {
            var row = PostgisDataStore.ReadRow(reader, reader.FieldCount);
            features.Add(PostgisRowMapper.MapRow(description.Schema, identity, row, ordinal++));
            if (features.Count >= BatchSize)
            {
                batches.Add(new FeatureBatch((FeatureSchema)description.Schema, features.ToArray()));
                features.Clear();
            }
        }

        if (features.Count > 0 || batches.Count == 0)
        {
            batches.Add(new FeatureBatch((FeatureSchema)description.Schema, features.ToArray()));
        }

        return batches;
    }

    /// <summary>Opens a pooled connection for the ingest capability (ADR-0041); the caller owns the transaction and lifecycle.</summary>
    internal Task<NpgsqlConnection> OpenIngestConnectionAsync(CancellationToken cancellationToken) =>
        _store.Value.OpenConnectionAsync(cancellationToken);

    /// <summary>Opens the connection a store-side edit runs on: the transaction handle's connection, or a fresh autocommit one (ADR-0037).</summary>
    internal async Task<PostgisEditSession> OpenEditSessionAsync(string? transaction, CancellationToken cancellationToken)
    {
        if (_transactions.TryGetValue(transaction ?? string.Empty, out var entry))
        {
            return new PostgisEditSession(entry.Connection, entry.Transaction, ownsConnection: false);
        }

        if (transaction is not null)
        {
            throw SpatialException.BadArguments($"Unknown transaction '{transaction}'.");
        }

        var connection = await _store.Value.OpenConnectionAsync(cancellationToken);
        return new PostgisEditSession(connection, transaction: null, ownsConnection: true);
    }

    internal static PostgisDatasetName ParseDataset(string dataset)
    {
        if (!PostgisDatasetName.TryParse(dataset, out var name, out var reason))
        {
            throw SpatialException.BadArguments($"Invalid dataset identifier: {reason}");
        }

        return name;
    }

    private static int[] IdentityIndexes(DatasetDescription description) =>
        description.IdColumns.Select(column => description.Schema.IndexOf(column))
            .Where(index => index >= 0)
            .ToArray();

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

    private sealed record TransactionEntry(NpgsqlConnection Connection, NpgsqlTransaction Transaction)
    {
        public async ValueTask DisposeAsync()
        {
            await Transaction.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
