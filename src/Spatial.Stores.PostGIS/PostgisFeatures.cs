using Npgsql;
using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Stores.PostGIS.Core;
using Spatial.Stores.PostGIS.Data;
using CoreBoundingBox = Spatial.Contracts.BoundingBox;

namespace Spatial.Stores.PostGIS;

/// <summary>
/// The feature face of a <see cref="PostgisStore"/> (ADR-0033): scan, spatial
/// query, read-by-identity and append. Every read describes the dataset first,
/// so the discovered schema, identity columns and SRID drive the SQL
/// (<see cref="PostgisQueries"/>) and the row mapping
/// (<see cref="PostgisRowMapper"/>); results stream back as canonical
/// <see cref="FeatureBatch"/> pages.
/// </summary>
internal sealed class PostgisFeatures(PostgisStorage storage, PostgisCatalogue catalogue)
{
    private const int BatchSize = 512;

    /// <summary>Every feature of the dataset, in canonical batch pages.</summary>
    public async Task<IReadOnlyList<FeatureBatch>> ScanAsync(PostgisDatasetName name, CancellationToken cancellationToken)
    {
        var description = await catalogue.DescribeAsync(name, cancellationToken);
        return await ReadBatchesAsync(
            PostgisQueries.Select(name, description.Schema), [], description, cancellationToken);
    }

    /// <summary>The features inside a bounding box and matching a filter expression.</summary>
    public async Task<IReadOnlyList<FeatureBatch>> QueryAsync(
        PostgisDatasetName name, CoreBoundingBox? bbox, string? filter, CancellationToken cancellationToken)
    {
        var description = await catalogue.DescribeAsync(name, cancellationToken);
        var parameters = new List<object?>();
        var predicate = PostgisPredicate.Build(description, bbox, filter, parameters);
        return await ReadBatchesAsync(
            PostgisQueries.Query(name, description.Schema, predicate), parameters, description, cancellationToken);
    }

    /// <summary>The features the dataset's identity columns name, or nothing when it has no identity.</summary>
    public async Task<IReadOnlyList<Feature>> ByIdentityAsync(
        PostgisDatasetName name, IReadOnlyList<FeatureId> ids, CancellationToken cancellationToken)
    {
        var description = await catalogue.DescribeAsync(name, cancellationToken);
        if (description.IdColumns.Count == 0)
        {
            return [];
        }

        var batches = await ReadBatchesAsync(
            PostgisQueries.SelectByIdentity(name, description.Schema, description.IdColumns, ids.Count),
            PostgisIdentity.Parameters(description, ids),
            description,
            cancellationToken);
        return batches.SelectMany(batch => batch.Features).ToArray();
    }

    /// <summary>
    /// Appends a batch, and returns the rows written: inside the transaction a
    /// handle names, or in one autocommit transaction. The batch is rejected
    /// when the dataset is not writable with its schema.
    /// </summary>
    public async Task<int> WriteAsync(
        PostgisDatasetName name,
        DatasetDescription description,
        FeatureBatch batch,
        string? transaction,
        CancellationToken cancellationToken)
    {
        PostgisWriteOperations.CheckWritable(description, batch);
        return transaction is null
            ? await WriteAutocommitAsync(name, description, batch, cancellationToken)
            : await storage.Transactions.WriteAsync(transaction, name, description, batch, cancellationToken);
    }

    private async Task<int> WriteAutocommitAsync(
        PostgisDatasetName name, DatasetDescription description, FeatureBatch batch, CancellationToken cancellationToken)
    {
        await using var connection = await storage.OpenConnectionAsync(cancellationToken);
        await using var txn = await connection.BeginTransactionAsync(cancellationToken);
        var count = await PostgisWriteOperations.WriteOnAsync(
            connection, txn, name, description, batch, cancellationToken);
        await txn.CommitAsync(cancellationToken);
        return count;
    }

    private async Task<IReadOnlyList<FeatureBatch>> ReadBatchesAsync(
        string sql, IReadOnlyList<object?> parameters, DatasetDescription description, CancellationToken cancellationToken)
    {
        await using var connection = await storage.OpenConnectionAsync(cancellationToken);
        await using var reader = await PostgisDataStore.ExecuteReaderAsync(connection, sql, parameters, cancellationToken);
        return await ReadFeatureBatchesAsync(reader, description, cancellationToken);
    }

    /// <summary>Streams the reader into fixed-size batches, always emitting at least one (possibly empty) batch.</summary>
    private static async Task<IReadOnlyList<FeatureBatch>> ReadFeatureBatchesAsync(
        NpgsqlDataReader reader, DatasetDescription description, CancellationToken cancellationToken)
    {
        var identity = IdentityIndexes(description);
        var features = new List<Feature>(BatchSize);
        long ordinal = 0;
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = PostgisDataStore.ReadRow(reader, reader.FieldCount);
            features.Add(PostgisRowMapper.MapRow(description.Schema, identity, row, ordinal++));
        }

        return Batch(features, description);
    }

    /// <summary>Chunks the streamed features, with one empty batch when the reader produced none.</summary>
    private static FeatureBatch[] Batch(List<Feature> features, DatasetDescription description)
    {
        var schema = (FeatureSchema)description.Schema;
        return features.Count == 0
            ? [new FeatureBatch(schema, [])]
            : features.Chunk(BatchSize).Select(chunk => new FeatureBatch(schema, chunk)).ToArray();
    }

    private static int[] IdentityIndexes(DatasetDescription description) =>
        description.IdColumns.Select(column => description.Schema.IndexOf(column))
            .Where(index => index >= 0)
            .ToArray();
}
