using Npgsql;
using Spatial.Core.Features;
using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Providers;
using Spatial.PluginSdk.Resources;
using Spatial.PluginSdk.Streams;
using Spatial.Provider.PostGIS.Configuration;
using Spatial.Provider.PostGIS.Core;
using Spatial.Provider.PostGIS.Data;
using Spatial.Provider.PostGIS.Geometry;
using Spatial.Provider.PostGIS.Streams;

namespace Spatial.Provider.PostGIS;

/// <summary>
/// The invocation surface of the <c>postgis@1</c> provider (ADR-0028): a
/// capability dispatch table over the nine data-provider contracts, the
/// shared argument/error plumbing (dataset, batch, bbox, filter, transaction
/// reads; cancellation before the store; redacted failure mapping), and the
/// store/tracing state the handlers share. Every handler validates and
/// checks cancellation without touching the store, then delegates the
/// database work to small leaves so the whole DB path is covered by the
/// containerised integration suite while the pure logic is covered by unit
/// tests.
/// </summary>
internal sealed class PostgisRunner
{
    /// <summary>The default SRID for result tables created without an explicit one.</summary>
    private const int DefaultCreateSrid = 4326;

    private readonly PostgisConnectionConfiguration _configuration;
    private readonly Lazy<PostgisDataStore> _store;
    private readonly PostgisTransactionRegistry _transactions = new();
    private readonly Dictionary<CapabilityId, Func<CapabilityInvocation, ValueTask<CapabilityResult>>> _handlers;

    public PostgisRunner(PostgisConnectionConfiguration configuration)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _store = new Lazy<PostgisDataStore>(() => PostgisDataStore.Open(configuration));
        _handlers = new Dictionary<CapabilityId, Func<CapabilityInvocation, ValueTask<CapabilityResult>>>
        {
            [CatalogueListContract.Id] = CatalogueListAsync,
            [DatasetDescribeContract.Id] = DescribeAsync,
            [DatasetCreateContract.Id] = CreateAsync,
            [FeatureScanContract.Id] = ScanAsync,
            [FeatureQueryContract.Id] = QueryAsync,
            [FeatureWriteContract.Id] = WriteAsync,
            [TransactionBeginContract.Id] = BeginAsync,
            [TransactionCommitContract.Id] = CommitAsync,
            [TransactionRollbackContract.Id] = RollbackAsync,
        };
    }

    public static IReadOnlyList<CapabilityDescriptor> Descriptors { get; } =
    [
        CatalogueListContract.Descriptor,
        DatasetDescribeContract.Descriptor,
        DatasetCreateContract.Descriptor,
        FeatureScanContract.Descriptor,
        FeatureQueryContract.Descriptor,
        FeatureWriteContract.Descriptor,
        TransactionBeginContract.Descriptor,
        TransactionCommitContract.Descriptor,
        TransactionRollbackContract.Descriptor,
    ];

    public ValueTask<CapabilityResult> InvokeAsync(CapabilityInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        return _handlers.TryGetValue(invocation.Capability, out var handler)
            ? handler(invocation)
            : new ValueTask<CapabilityResult>(CapabilityResult.Failure(NotServed(invocation)));
    }

    private static CapabilityError NotServed(CapabilityInvocation invocation) =>
        CapabilityError.ContractViolation($"{invocation.Capability} is not served by postgis@1.");

    // ---- Catalogue ----

    private async ValueTask<CapabilityResult> CatalogueListAsync(CapabilityInvocation invocation)
    {
        if (ReadPattern(invocation, out var pattern) is { } error)
        {
            return CapabilityResult.Failure(error);
        }

        if (invocation.Facilities is not { } facilities)
        {
            return CapabilityResult.Failure(NeedsFacilities(invocation));
        }

        if (CheckCancelled(invocation) is { } cancelled)
        {
            return CapabilityResult.Failure(cancelled);
        }

        if (RequireConfigured(invocation) is { } unavailable)
        {
            return CapabilityResult.Failure(unavailable);
        }


        try
        {
            var channel = facilities.Streams.Create(ProviderResourceKinds.CatalogueStream, FeatureBatchStream.StreamCapacity);
            _ = EmitCatalogueAsync(invocation.Capability, channel, pattern, invocation.CancellationToken);
            return CapabilityResult.Success(channel.Handle);
        }
        catch (Exception exception)
        {
            return CapabilityResult.Failure(MapFailure(invocation, exception));
        }
    }

    private async Task EmitCatalogueAsync(CapabilityId capability, StreamChannel channel, string? pattern, CancellationToken token)
    {
        try
        {
            await using var connection = await _store.Value.OpenConnectionAsync(token);
            var parameters = pattern is null ? [] : new object?[] { pattern };
            await using var reader = await PostgisDataStore.ExecuteReaderAsync(connection, PostgisQueries.Catalogue(pattern), parameters, token);
            await EmitCatalogueLoopAsync(channel, reader, token);
            channel.Writer.Complete();
        }
        catch (Exception exception)
        {
            channel.Writer.Complete(PostgisDiagnostics.StreamFailure(capability, exception, _configuration));
        }
    }

    private static async Task EmitCatalogueLoopAsync(StreamChannel channel, NpgsqlDataReader reader, CancellationToken token)
    {
        while (await reader.ReadAsync(token))
        {
            var row = PostgisDataStore.ReadRow(reader, reader.FieldCount);
            await channel.Writer.WriteAsync(DatasetMetadataJson.WriteSummary(PostgisSchemaDiscovery.SummaryFromRow(row)), token);
        }
    }

    // ---- Dataset describe + create ----

    private async ValueTask<CapabilityResult> DescribeAsync(CapabilityInvocation invocation)
    {
        if (ReadDataset(invocation, out var dataset) is { } error)
        {
            return CapabilityResult.Failure(error);
        }

        if (invocation.Facilities is not { } facilities)
        {
            return CapabilityResult.Failure(NeedsFacilities(invocation));
        }

        if (CheckCancelled(invocation) is { } cancelled)
        {
            return CapabilityResult.Failure(cancelled);
        }

        if (RequireConfigured(invocation) is { } unavailable)
        {
            return CapabilityResult.Failure(unavailable);
        }


        try
        {
            var description = await DescribeDatasetAsync(invocation, dataset, invocation.CancellationToken);
            var channel = facilities.Streams.Create(ProviderResourceKinds.DatasetStream, 1);
            _ = EmitDescriptionAsync(invocation, channel, description, invocation.CancellationToken);
            return CapabilityResult.Success(channel.Handle);
        }
        catch (Exception exception)
        {
            return CapabilityResult.Failure(MapFailure(invocation, exception));
        }
    }

    private async Task EmitDescriptionAsync(CapabilityInvocation invocation, StreamChannel channel, DatasetDescription description, CancellationToken token)
    {
        try
        {
            await channel.Writer.WriteAsync(DatasetMetadataJson.WriteDescription(description), token);
            channel.Writer.Complete();
        }
        catch (Exception exception)
        {
            channel.Writer.Complete(PostgisDiagnostics.StreamFailure(invocation.Capability, exception, _configuration));
        }
    }

    private async ValueTask<CapabilityResult> CreateAsync(CapabilityInvocation invocation)
    {
        if (ReadDataset(invocation, out var dataset) is { } error)
        {
            return CapabilityResult.Failure(error);
        }

        if (ReadSrid(invocation, out var srid) is { } sridError)
        {
            return CapabilityResult.Failure(sridError);
        }

        if (ReadBatch(invocation, out var batch) is { } batchError)
        {
            return CapabilityResult.Failure(batchError);
        }

        if (ValidateBatchSchema(batch.Schema, invocation) is { } schemaError)
        {
            return CapabilityResult.Failure(schemaError);
        }

        if (CheckCancelled(invocation) is { } cancelled)
        {
            return CapabilityResult.Failure(cancelled);
        }

        if (RequireConfigured(invocation) is { } unavailable)
        {
            return CapabilityResult.Failure(unavailable);
        }


        try
        {
            await using var connection = await _store.Value.OpenConnectionAsync(invocation.CancellationToken);
            await PostgisDataStore.ExecuteNonQueryAsync(
                connection, PostgisQueries.CreateTable(dataset, batch.Schema, srid), [], invocation.CancellationToken);
            return CapabilityResult.Success(dataset.Qualified);
        }
        catch (Exception exception)
        {
            return CapabilityResult.Failure(MapFailure(invocation, exception));
        }
    }

    // ---- Feature scan + query ----

    private async ValueTask<CapabilityResult> ScanAsync(CapabilityInvocation invocation)
    {
        if (ReadDataset(invocation, out var dataset) is { } error)
        {
            return CapabilityResult.Failure(error);
        }

        if (invocation.Facilities is not { } facilities)
        {
            return CapabilityResult.Failure(NeedsFacilities(invocation));
        }

        if (CheckCancelled(invocation) is { } cancelled)
        {
            return CapabilityResult.Failure(cancelled);
        }

        if (RequireConfigured(invocation) is { } unavailable)
        {
            return CapabilityResult.Failure(unavailable);
        }


        try
        {
            var description = await DescribeDatasetAsync(invocation, dataset, invocation.CancellationToken);
            var channel = facilities.Streams.Create(ProviderResourceKinds.FeatureStream, FeatureBatchStream.StreamCapacity);
            _ = EmitScanAsync(
                invocation, channel, description.Schema, IdentityIndexes(description),
                PostgisQueries.Select(dataset, description.Schema), null, invocation.CancellationToken);
            return CapabilityResult.Success(channel.Handle);
        }
        catch (Exception exception)
        {
            return CapabilityResult.Failure(MapFailure(invocation, exception));
        }
    }

    private async ValueTask<CapabilityResult> QueryAsync(CapabilityInvocation invocation)
    {
        if (ReadDataset(invocation, out var dataset) is { } error)
        {
            return CapabilityResult.Failure(error);
        }

        if (ReadBoundingBox(invocation, out var boundingBox) is { } boxError)
        {
            return CapabilityResult.Failure(boxError);
        }

        if (ReadFilter(invocation, out var filterText) is { } filterError)
        {
            return CapabilityResult.Failure(filterError);
        }

        if (ParseFilter(invocation, filterText, out var filter) is { } parseError)
        {
            return CapabilityResult.Failure(parseError);
        }

        if (invocation.Facilities is not { } facilities)
        {
            return CapabilityResult.Failure(NeedsFacilities(invocation));
        }

        if (CheckCancelled(invocation) is { } cancelled)
        {
            return CapabilityResult.Failure(cancelled);
        }

        if (RequireConfigured(invocation) is { } unavailable)
        {
            return CapabilityResult.Failure(unavailable);
        }


        try
        {
            var description = await DescribeDatasetAsync(invocation, dataset, invocation.CancellationToken);
            if (!TryBuildPredicate(invocation, description, boundingBox, filter, out var predicate, out var parameters, out var buildError))
            {
                return CapabilityResult.Failure(buildError!);
            }

            var channel = facilities.Streams.Create(ProviderResourceKinds.FeatureStream, FeatureBatchStream.StreamCapacity);
            _ = EmitScanAsync(
                invocation, channel, description.Schema, IdentityIndexes(description),
                PostgisQueries.Query(dataset, description.Schema, predicate), parameters, invocation.CancellationToken);
            return CapabilityResult.Success(channel.Handle);
        }
        catch (Exception exception)
        {
            return CapabilityResult.Failure(MapFailure(invocation, exception));
        }
    }

    /// <summary>
    /// Streams one scan/query result: opens the connection and reader, emits
    /// canonical feature batches and completes the stream (or fails it with a
    /// redacted error). All heavy logic lives in the small helpers below so
    /// this DB-only path stays bounded.
    /// </summary>
    private async Task EmitScanAsync(
        CapabilityInvocation invocation,
        StreamChannel channel,
        FeatureSchema schema,
        IReadOnlyList<int> identityIndexes,
        string sql,
        IReadOnlyList<object?>? parameters,
        CancellationToken token)
    {
        try
        {
            await using var connection = await _store.Value.OpenConnectionAsync(token);
            await using var reader = await PostgisDataStore.ExecuteReaderAsync(connection, sql, parameters ?? [], token);
            await EmitLoopAsync(reader, schema, identityIndexes, channel, token);
            channel.Writer.Complete();
        }
        catch (Exception exception)
        {
            channel.Writer.Complete(PostgisDiagnostics.StreamFailure(invocation.Capability, exception, _configuration));
        }
    }

    private static async Task EmitLoopAsync(
        NpgsqlDataReader reader,
        FeatureSchema schema,
        IReadOnlyList<int> identityIndexes,
        StreamChannel channel,
        CancellationToken token)
    {
        var features = new List<Feature>(FeatureBatchStream.FeaturesPerBatch);
        long ordinal = 0;
        while (await reader.ReadAsync(token))
        {
            var row = PostgisDataStore.ReadRow(reader, reader.FieldCount);
            await CollectAsync(features, schema, channel, PostgisRowMapper.MapRow(schema, identityIndexes, row, ordinal), token);
            ordinal++;
        }

        await FinalFlushAsync(features, schema, channel, token);
    }

    private static async Task CollectAsync(
        List<Feature> features,
        FeatureSchema schema,
        StreamChannel channel,
        Feature feature,
        CancellationToken token)
    {
        features.Add(feature);
        if (features.Count < FeatureBatchStream.FeaturesPerBatch)
        {
            return;
        }

        await FlushAsync(features, schema, channel, token);
    }

    private static async Task FinalFlushAsync(List<Feature> features, FeatureSchema schema, StreamChannel channel, CancellationToken token)
    {
        if (features.Count == 0)
        {
            return;
        }

        await FlushAsync(features, schema, channel, token);
    }

    private static async Task FlushAsync(List<Feature> features, FeatureSchema schema, StreamChannel channel, CancellationToken token)
    {
        var bytes = FeatureBatchStream.Encode(schema, features);
        await channel.Writer.WriteAsync(bytes, token);
        features.Clear();
    }

    // ---- Feature write ----

    private async ValueTask<CapabilityResult> WriteAsync(CapabilityInvocation invocation)
    {
        if (ReadDataset(invocation, out var dataset) is { } error)
        {
            return CapabilityResult.Failure(error);
        }

        if (ReadOptionalTransaction(invocation, out var transactionId) is { } txError)
        {
            return CapabilityResult.Failure(txError);
        }

        if (ReadBatch(invocation, out var batch) is { } batchError)
        {
            return CapabilityResult.Failure(batchError);
        }

        if (CheckCancelled(invocation) is { } cancelled)
        {
            return CapabilityResult.Failure(cancelled);
        }

        if (RequireConfigured(invocation) is { } unavailable)
        {
            return CapabilityResult.Failure(unavailable);
        }


        try
        {
            var description = await DescribeDatasetAsync(invocation, dataset, invocation.CancellationToken);
            if (!description.Schema.IsDecodableFrom(batch.Schema))
            {
                return CapabilityResult.Failure(NotDecodable(invocation, dataset, description));
            }

            var appended = await AppendAsync(invocation, dataset, description, batch, transactionId);
            return CapabilityResult.Success(appended);
        }
        catch (Exception exception)
        {
            return CapabilityResult.Failure(MapFailure(invocation, exception));
        }
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
            if (!_transactions.TryGet(id, out var state))
            {
                throw new PostgisInactiveTransactionException("the transaction is no longer active; begin a new one and retry the write.");
            }

            return await AppendAsync(dataset, state.Connection, description.Srid, batch, invocation.CancellationToken);
        }

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

    // ---- Transactions ----

    private async ValueTask<CapabilityResult> BeginAsync(CapabilityInvocation invocation)
    {
        if (invocation.Facilities is not { } facilities)
        {
            return CapabilityResult.Failure(NeedsFacilities(invocation));
        }

        if (CheckCancelled(invocation) is { } cancelled)
        {
            return CapabilityResult.Failure(cancelled);
        }

        if (RequireConfigured(invocation) is { } unavailable)
        {
            return CapabilityResult.Failure(unavailable);
        }


        try
        {
            var connection = await _store.Value.OpenConnectionAsync(invocation.CancellationToken);
            var transaction = await connection.BeginTransactionAsync(invocation.CancellationToken);
            var handle = facilities.Resources.Create(ProviderResourceKinds.Transaction);
            if (!_transactions.TryAdd(handle.Id, connection, transaction))
            {
                await transaction.DisposeAsync();
                await connection.DisposeAsync();
                return CapabilityResult.Failure(PostgisDiagnostics.InvalidArgument(invocation.Capability, "a transaction for this handle already exists."));
            }

            return CapabilityResult.Success(handle);
        }
        catch (Exception exception)
        {
            return CapabilityResult.Failure(MapFailure(invocation, exception));
        }
    }

    private async ValueTask<CapabilityResult> CommitAsync(CapabilityInvocation invocation) =>
        await EndTransactionAsync(invocation, commit: true);

    private async ValueTask<CapabilityResult> RollbackAsync(CapabilityInvocation invocation) =>
        await EndTransactionAsync(invocation, commit: false);

    private async ValueTask<CapabilityResult> EndTransactionAsync(CapabilityInvocation invocation, bool commit)
    {
        if (ReadTransactionHandle(invocation, out var handle) is { } error)
        {
            return CapabilityResult.Failure(error);
        }

        if (CheckCancelled(invocation) is { } cancelled)
        {
            return CapabilityResult.Failure(cancelled);
        }

        if (RequireConfigured(invocation) is { } unavailable)
        {
            return CapabilityResult.Failure(unavailable);
        }


        try
        {
            if (!_transactions.TryTake(handle, out var state))
            {
                return CapabilityResult.Failure(PostgisDiagnostics.InvalidArgument(
                    invocation.Capability, "the transaction is no longer active; begin a new one."));
            }

            if (commit)
            {
                await state.Transaction.CommitAsync(invocation.CancellationToken);
            }
            else
            {
                await state.Transaction.RollbackAsync(invocation.CancellationToken);
            }

            await state.Connection.DisposeAsync();
            return CapabilityResult.Success(true);
        }
        catch (Exception exception)
        {
            return CapabilityResult.Failure(MapFailure(invocation, exception));
        }
    }

    // ---- Shared discovery + plumbing ----

    private async Task<DatasetDescription> DescribeDatasetAsync(
        CapabilityInvocation invocation,
        PostgisDatasetName dataset,
        CancellationToken token)
    {
        await using var connection = await _store.Value.OpenConnectionAsync(token);
        var columns = await ReadColumnsAsync(connection, dataset, token);
        var geometries = await ReadGeometriesAsync(connection, dataset, token);
        var primaryKeys = await ReadPrimaryKeysAsync(connection, dataset, token);
        var rowEstimate = await ReadRowEstimateAsync(connection, dataset, token);
        if (PostgisSchemaDiscovery.TryBuild(dataset, columns, geometries, primaryKeys, rowEstimate, out var description, out var reason))
        {
            return description;
        }

        throw new PostgisUnknownDatasetException($"{invocation.Capability} cannot read '{dataset}': {reason}");
    }

    private static async Task<IReadOnlyList<PostgisSchemaDiscovery.ColumnRow>> ReadColumnsAsync(
        NpgsqlConnection connection,
        PostgisDatasetName dataset,
        CancellationToken token)
    {
        var rows = await PostgisDataStore.ReadRowsAsync(connection, PostgisQueries.ColumnsMetadata(), DatasetParameters(dataset), token);
        var columns = new List<PostgisSchemaDiscovery.ColumnRow>(rows.Count);
        foreach (var row in rows)
        {
            columns.Add(new PostgisSchemaDiscovery.ColumnRow(
                (string)row[0]!, (string)row[1]!, string.Equals((string)row[2]!, "YES", StringComparison.Ordinal), (int)row[3]!));
        }

        return columns;
    }

    private static async Task<IReadOnlyList<PostgisSchemaDiscovery.GeometryRow>> ReadGeometriesAsync(
        NpgsqlConnection connection,
        PostgisDatasetName dataset,
        CancellationToken token)
    {
        var rows = await PostgisDataStore.ReadRowsAsync(connection, PostgisQueries.GeometryColumnsMetadata(), DatasetParameters(dataset), token);
        var geometries = new List<PostgisSchemaDiscovery.GeometryRow>(rows.Count);
        foreach (var row in rows)
        {
            geometries.Add(new PostgisSchemaDiscovery.GeometryRow((string)row[0]!, (int)row[1]!, (string)row[2]!));
        }

        return geometries;
    }

    private static async Task<IReadOnlyList<string>> ReadPrimaryKeysAsync(
        NpgsqlConnection connection,
        PostgisDatasetName dataset,
        CancellationToken token)
    {
        var rows = await PostgisDataStore.ReadRowsAsync(connection, PostgisQueries.PrimaryKeyColumns(), DatasetParameters(dataset), token);
        var keys = new List<string>(rows.Count);
        foreach (var row in rows)
        {
            keys.Add((string)row[0]!);
        }

        return keys;
    }

    private static async Task<long> ReadRowEstimateAsync(
        NpgsqlConnection connection,
        PostgisDatasetName dataset,
        CancellationToken token)
    {
        var rows = await PostgisDataStore.ReadRowsAsync(connection, PostgisQueries.RowEstimate(), DatasetParameters(dataset), token);
        return rows.Count == 0
            ? 0
            : Convert.ToInt64(rows[0][0], System.Globalization.CultureInfo.InvariantCulture);
    }

    private static object?[] DatasetParameters(PostgisDatasetName dataset) => [dataset.Schema, dataset.Table];

    /// <summary>The schema indexes of the primary-key columns (empty when the table has none).</summary>
    private static int[] IdentityIndexes(DatasetDescription description)
    {
        var indexes = new List<int>(description.IdColumns.Count);
        foreach (var column in description.IdColumns)
        {
            var index = description.Schema.IndexOf(column);
            if (index >= 0)
            {
                indexes.Add(index);
            }
        }

        return indexes.ToArray();
    }

    private static bool TryBuildPredicate(
        CapabilityInvocation invocation,
        DatasetDescription description,
        BoundingBox? boundingBox,
        FilterExpression? filter,
        out string? predicate,
        out IReadOnlyList<object?> parameters,
        out CapabilityError? error)
    {
        var values = new List<object?>();
        var parts = new List<string>(2);
        if (boundingBox is { } box)
        {
            parts.Add(PostgisFilterSql.BoundingBox(
                description.GeometryColumn, description.Srid, box.MinX, box.MinY, box.MaxX, box.MaxY, values));
        }

        if (filter is not null)
        {
            if (!PostgisFilterSql.TryBuild(filter, description.Schema, values, out var filterSql, out var filterError))
            {
                error = PostgisDiagnostics.InvalidArgument(invocation.Capability, $"the filter is not valid: {filterError}");
                predicate = null;
                parameters = [];
                return false;
            }

            parts.Add(filterSql);
        }

        predicate = parts.Count == 0 ? null : string.Join(" AND ", parts);
        parameters = values;
        error = null;
        return true;
    }

    private CapabilityError MapFailure(CapabilityInvocation invocation, Exception exception)
    {
        if (exception is PostgisUnknownDatasetException unknown)
        {
            return PostgisDiagnostics.InvalidArgument(invocation.Capability, unknown.Message);
        }

        if (exception is PostgisCrsMismatchException mismatch)
        {
            return PostgisDiagnostics.InvalidArgument(invocation.Capability, mismatch.Message);
        }

        if (exception is PostgisInactiveTransactionException inactive)
        {
            return PostgisDiagnostics.InvalidArgument(invocation.Capability, inactive.Message);
        }

        return PostgisDiagnostics.ProviderFailure(_configuration, invocation.Capability, exception);
    }

    // ---- Argument readers (all pure, all unit-testable) ----

    /// <summary>The no-connection-configuration guard: valid work on an unconfigured provider is unavailable (redacted, actionable).</summary>
    private CapabilityError? RequireConfigured(CapabilityInvocation invocation) =>
        _configuration.IsConfigured
            ? null
            : PostgisDiagnostics.Unavailable(invocation.Capability);

    private static CapabilityError? CheckCancelled(CapabilityInvocation invocation) =>
        invocation.CancellationToken.IsCancellationRequested
            ? CapabilityError.Cancelled(invocation.Capability)
            : null;

    private static CapabilityError? ReadDataset(CapabilityInvocation invocation, out PostgisDatasetName dataset)
    {
        dataset = default;
        if (!invocation.TryGetArgument<string>(ProviderArguments.Dataset, out var text))
        {
            return PostgisDiagnostics.InvalidArgument(
                invocation.Capability, "a 'dataset' identifier (schema.table or table) is required.");
        }

        if (!PostgisDatasetName.TryParse(text, out dataset, out var reason))
        {
            return PostgisDiagnostics.InvalidArgument(invocation.Capability, reason);
        }

        return null;
    }

    private static CapabilityError? ReadBatch(CapabilityInvocation invocation, out FeatureBatch batch)
    {
        batch = null!;
        if (!invocation.TryGetArgument<byte[]>(ProviderArguments.Batch, out var bytes))
        {
            return PostgisDiagnostics.InvalidArgument(
                invocation.Capability, "'batch' must carry a canonical feature batch (FeatureBatchCodec v1 bytes).");
        }

        if (FeatureBatchStream.TryDecode(bytes, out batch, out var reason))
        {
            return null;
        }

        return PostgisDiagnostics.InvalidArgument(invocation.Capability, $"the 'batch' is not a valid canonical feature batch: {reason}");
    }

    private static CapabilityError? ReadSrid(CapabilityInvocation invocation, out int srid)
    {
        srid = DefaultCreateSrid;
        if (!invocation.Arguments.ContainsKey(ProviderArguments.Srid))
        {
            return null;
        }

        if (invocation.TryGetArgument<int>(ProviderArguments.Srid, out var value) && value >= 0)
        {
            srid = value;
            return null;
        }

        return PostgisDiagnostics.InvalidArgument(invocation.Capability, "'srid' must be a non-negative int32 when provided.");
    }

    private static CapabilityError? ReadBoundingBox(CapabilityInvocation invocation, out BoundingBox? boundingBox)
    {
        boundingBox = null;
        var names = new[] { ProviderArguments.MinX, ProviderArguments.MinY, ProviderArguments.MaxX, ProviderArguments.MaxY };
        var present = names.Where(name => invocation.Arguments.ContainsKey(name)).ToArray();
        if (present.Length == 0)
        {
            return null;
        }

        if (present.Length != 4)
        {
            return PostgisDiagnostics.InvalidArgument(
                invocation.Capability, "the bounding box needs all four bounds: minx, miny, maxx, maxy.");
        }

        if (!TryReadNumber(invocation, ProviderArguments.MinX, out var minx)
            || !TryReadNumber(invocation, ProviderArguments.MinY, out var miny)
            || !TryReadNumber(invocation, ProviderArguments.MaxX, out var maxx)
            || !TryReadNumber(invocation, ProviderArguments.MaxY, out var maxy))
        {
            return PostgisDiagnostics.InvalidArgument(
                invocation.Capability, "the bounding-box bounds minx/miny/maxx/maxy must be numbers.");
        }

        if (!double.IsFinite(minx) || !double.IsFinite(miny) || !double.IsFinite(maxx) || !double.IsFinite(maxy)
            || minx > maxx || miny > maxy)
        {
            return PostgisDiagnostics.InvalidArgument(
                invocation.Capability, "the bounding box is invalid: bounds must be finite with minx <= maxx and miny <= maxy.");
        }

        boundingBox = new BoundingBox(minx, miny, maxx, maxy);
        return null;
    }

    /// <summary>Reads one bbox bound as a double, accepting the wire's integral JSON number forms too (int/long/long).</summary>
    private static bool TryReadNumber(CapabilityInvocation invocation, string name, out double value)
    {
        if (invocation.TryGetArgument<double>(name, out value))
        {
            return true;
        }

        if (invocation.TryGetArgument<int>(name, out var integer))
        {
            value = integer;
            return true;
        }

        if (invocation.TryGetArgument<long>(name, out var longInteger))
        {
            value = longInteger;
            return true;
        }

        value = 0;
        return false;
    }

    private static CapabilityError? ReadFilter(CapabilityInvocation invocation, out string? filterText)
    {
        filterText = null;
        if (!invocation.Arguments.ContainsKey(ProviderArguments.Filter))
        {
            return null;
        }

        if (invocation.TryGetArgument<string>(ProviderArguments.Filter, out var text))
        {
            filterText = text;
            return null;
        }

        return PostgisDiagnostics.InvalidArgument(invocation.Capability, "'filter' must be a string expression.");
    }

    private static CapabilityError? ParseFilter(CapabilityInvocation invocation, string? filterText, out FilterExpression? filter)
    {
        filter = null;
        if (filterText is null)
        {
            return null;
        }

        if (PostgisFilterParser.TryParse(filterText, out var expression, out var error))
        {
            filter = expression;
            return null;
        }

        return PostgisDiagnostics.InvalidArgument(invocation.Capability, $"the filter is not valid: {error}");
    }

    private static CapabilityError? ReadPattern(CapabilityInvocation invocation, out string? pattern)
    {
        pattern = null;
        if (!invocation.Arguments.ContainsKey(ProviderArguments.Pattern))
        {
            return null;
        }

        if (invocation.TryGetArgument<string>(ProviderArguments.Pattern, out var text))
        {
            pattern = text;
            return null;
        }

        return PostgisDiagnostics.InvalidArgument(invocation.Capability, "'pattern' must be a string.");
    }

    private static CapabilityError? ReadOptionalTransaction(CapabilityInvocation invocation, out ResourceId? transactionId)
    {
        transactionId = null;
        if (!invocation.Arguments.ContainsKey(ProviderArguments.Transaction))
        {
            return null;
        }

        if (invocation.TryGetArgument<ResourceHandle>(ProviderArguments.Transaction, out var handle))
        {
            transactionId = handle.Id;
            return null;
        }

        return PostgisDiagnostics.InvalidArgument(
            invocation.Capability, "'transaction' must be a transaction handle from spatial.transaction.begin@1.");
    }

    private static CapabilityError? ReadTransactionHandle(CapabilityInvocation invocation, out ResourceId handle)
    {
        handle = default;
        if (invocation.TryGetArgument<ResourceHandle>(ProviderArguments.Transaction, out var transaction))
        {
            handle = transaction.Id;
            return null;
        }

        return PostgisDiagnostics.InvalidArgument(
            invocation.Capability, "a 'transaction' handle from spatial.transaction.begin@1 is required.");
    }

    private static CapabilityError? ValidateBatchSchema(FeatureSchema schema, CapabilityInvocation invocation)
    {
        foreach (var field in schema.Fields)
        {
            if (PostgisDatasetName.IsValidIdentifier(field.Name))
            {
                continue;
            }

            return PostgisDiagnostics.InvalidArgument(
                invocation.Capability,
                $"the field name '{field.Name}' is not a valid column identifier; only lowercase [a-z0-9_] names are allowed.");
        }

        if (schema.Fields.All(field => field.Kind != AttributeKind.Geometry))
        {
            return PostgisDiagnostics.InvalidArgument(
                invocation.Capability, "the defining batch has no geometry field; a spatial result table needs one.");
        }

        return null;
    }

    private static CapabilityError NotDecodable(CapabilityInvocation invocation, PostgisDatasetName dataset, DatasetDescription description)
    {
        var fields = string.Join(", ", description.Schema.Fields.Select(field => $"'{field.Name}'"));
        return PostgisDiagnostics.InvalidArgument(
            invocation.Capability,
            $"the batch schema is not decodable from '{dataset}' (fields: {fields}); write a batch carrying a prefix of the dataset's fields.");
    }

    private static CapabilityError NeedsFacilities(CapabilityInvocation invocation) =>
        PostgisDiagnostics.InvalidArgument(invocation.Capability, "the invocation has no runtime facilities; only the runtime can mint streams and resources.");

    /// <summary>The validated bounding-box filter of a query invocation.</summary>
    private readonly record struct BoundingBox(double MinX, double MinY, double MaxX, double MaxY);
}
