using Npgsql;
using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Stores.PostGIS.Core;
using Spatial.Stores.PostGIS.Data;

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

    /// <summary>
    /// The features a query plan selects. Every member of the plan is
    /// expressed in SQL — the identity restriction as an OR-group of identity
    /// tuples (ADR-0038), the bounding box and the attribute predicate as one
    /// parameterised fragment (ADR-0074) — so the plan is answered by the
    /// database in one read and nothing is evaluated a second time in memory.
    /// An attribute predicate that compares text says which order it wants, so
    /// a filter cannot inherit the database's collation where the order
    /// refuses to (ADR-0123).
    /// </summary>
    public async Task<IReadOnlyList<FeatureBatch>> QueryAsync(
        PostgisDatasetName name, FeatureQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var description = await catalogue.DescribeAsync(name, cancellationToken);
        if (query.Ids is { Count: > 0 })
        {
            if (description.IdColumns.Count == 0)
            {
                return [new FeatureBatch(description.Schema, [])];
            }

            var identityParameters = PostgisIdentity.Parameters(description, query.Ids);
            var byteOrderText = await ByteOrderTextAsync(description, query, cancellationToken);
            var identityPredicate = PostgisPredicateSql.Build(
                description, query.BoundingBox, query.Where, byteOrderText, identityParameters);
            return await ReadBatchesAsync(
                PostgisQueries.SelectByIdentity(
                    name, description.Schema, description.IdColumns, query.Ids.Count, byteOrderText, identityPredicate),
                identityParameters,
                description,
                cancellationToken);
        }

        var parameters = new List<object?>();
        var predicate = PostgisPredicateSql.Build(
            description, query.BoundingBox, query.Where, await ByteOrderTextAsync(description, query, cancellationToken), parameters);
        return await ReadBatchesAsync(
            PostgisQueries.Query(name, description.Schema, predicate), parameters, description, cancellationToken);
    }

    /// <summary>
    /// Whether this database already compares text by bytes, read only when the
    /// plan's restriction compares a text column at all — the attribute
    /// predicate's own (ADR-0123) and the identity restriction's (ADR-0126).
    /// Both are the one part of a <c>WHERE</c> a collation can change, and a
    /// plan over a number and a bounding box cannot use the answer.
    /// </summary>
    private Task<bool> ByteOrderTextAsync(
        DatasetDescription description, FeatureQuery query, CancellationToken cancellationToken) =>
        ComparesText(description, query)
            ? storage.ByteOrderTextAsync(cancellationToken)
            : Task.FromResult(false);

    /// <summary>Whether the plan's restriction compares a text column, in either half of it.</summary>
    private static bool ComparesText(DatasetDescription description, FeatureQuery query) =>
        (query.Ids is { Count: > 0 } && PostgisIdentity.ComparesText(description))
        || (query.Where is { } where && PostgisPredicateSql.ComparesText(where, description.Schema));

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
            PostgisQueries.SelectByIdentity(
                name,
                description.Schema,
                description.IdColumns,
                ids.Count,
                // A lookup is a comparison against the identity, so it states
                // the byte order too (ADR-0126) — and reads the answer only when
                // the identity has a text column that can be changed by it.
                PostgisIdentity.ComparesText(description)
                    ? await storage.ByteOrderTextAsync(cancellationToken)
                    : false),
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
