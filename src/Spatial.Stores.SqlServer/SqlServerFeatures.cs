using Microsoft.Data.SqlClient;
using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Stores.SqlServer.Core;
using Spatial.Stores.SqlServer.Data;

namespace Spatial.Stores.SqlServer;

/// <summary>
/// The feature face of a <see cref="SqlServerStore"/> (ADR-0033 as the SQL
/// Server provider follows it): scan, spatial query, read-by-identity and
/// append. Every read describes the dataset first, so the discovered schema,
/// identity columns and SRID drive the T-SQL (<see cref="SqlServerQueries"/>)
/// and the row mapping (<see cref="SqlServerRowMapper"/>); results stream back
/// as canonical <see cref="FeatureBatch"/> pages.
/// </summary>
internal sealed class SqlServerFeatures(SqlServerStorage storage, SqlServerCatalogue catalogue)
{
    private const int BatchSize = 512;

    /// <summary>Every feature of the dataset, in canonical batch pages.</summary>
    public async Task<IReadOnlyList<FeatureBatch>> ScanAsync(
        SqlServerDatasetName name, CancellationToken cancellationToken)
    {
        var description = await catalogue.DescribeAsync(name, cancellationToken);
        return await ReadBatchesAsync(
            SqlServerQueries.Select(name, description.Schema), [], description, cancellationToken);
    }

    /// <summary>
    /// The features a query plan selects. Every member of the plan is
    /// expressed in T-SQL — the identity restriction as an OR-group of
    /// identity tuples (ADR-0038), the bounding box and the attribute
    /// predicate as one parameterised fragment (ADR-0074) — so the plan is
    /// answered by the database in one read.
    /// </summary>
    public async Task<IReadOnlyList<FeatureBatch>> QueryAsync(
        SqlServerDatasetName name, FeatureQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var description = await catalogue.DescribeAsync(name, cancellationToken);
        if (query.Ids is { Count: > 0 })
        {
            if (description.IdColumns.Count == 0)
            {
                return [new FeatureBatch(description.Schema, [])];
            }

            // The identity values are bound first, so the plan's own
            // parameters continue their numbering after them.
            var identity = SqlServerIdentity.Parameters(description, query.Ids);
            var identityPredicate = SqlServerPredicateSql.Build(description, query.BoundingBox, query.Where, identity);
            return await ReadBatchesAsync(
                SqlServerQueries.SelectByIdentity(
                    name, description.Schema, description.IdColumns, query.Ids.Count, identityPredicate),
                identity,
                description,
                cancellationToken);
        }

        var parameters = new List<object?>();
        var predicate = SqlServerPredicateSql.Build(description, query.BoundingBox, query.Where, parameters);
        return await ReadBatchesAsync(
            SqlServerQueries.Query(name, description.Schema, predicate), parameters, description, cancellationToken);
    }

    /// <summary>The features the dataset's identity columns name, or nothing when it has no identity.</summary>
    public async Task<IReadOnlyList<Feature>> ByIdentityAsync(
        SqlServerDatasetName name, IReadOnlyList<FeatureId> ids, CancellationToken cancellationToken)
    {
        var description = await catalogue.DescribeAsync(name, cancellationToken);
        if (description.IdColumns.Count == 0)
        {
            return [];
        }

        var batches = await ReadBatchesAsync(
            SqlServerQueries.SelectByIdentity(name, description.Schema, description.IdColumns, ids.Count),
            SqlServerIdentity.Parameters(description, ids),
            description,
            cancellationToken);
        return [.. batches.SelectMany(batch => batch.Features)];
    }

    /// <summary>
    /// Appends a batch, and returns the rows written: inside the transaction a
    /// handle names, or in one autocommit transaction. The batch is rejected
    /// when the dataset is not writable with its schema.
    /// </summary>
    public async Task<int> WriteAsync(
        SqlServerDatasetName name,
        DatasetDescription description,
        FeatureBatch batch,
        string? transaction,
        CancellationToken cancellationToken)
    {
        SqlServerWriteOperations.CheckWritable(description, batch);
        return transaction is null
            ? await WriteAutocommitAsync(name, description, batch, cancellationToken)
            : await storage.Transactions.WriteAsync(transaction, name, description, batch, cancellationToken);
    }

    private async Task<int> WriteAutocommitAsync(
        SqlServerDatasetName name,
        DatasetDescription description,
        FeatureBatch batch,
        CancellationToken cancellationToken)
    {
        await using var connection = await storage.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var count = await SqlServerWriteOperations.WriteOnAsync(
            connection, transaction, name, description, batch, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return count;
    }

    private async Task<IReadOnlyList<FeatureBatch>> ReadBatchesAsync(
        string sql, IReadOnlyList<object?> parameters, DatasetDescription description, CancellationToken cancellationToken)
    {
        await using var connection = await storage.OpenConnectionAsync(cancellationToken);
        await using var reader = await SqlServerDataStore.ExecuteReaderAsync(connection, sql, parameters, cancellationToken);
        return await ReadFeatureBatchesAsync(reader, description, cancellationToken);
    }

    /// <summary>Streams the reader into fixed-size batches, always emitting at least one (possibly empty) batch.</summary>
    private static async Task<IReadOnlyList<FeatureBatch>> ReadFeatureBatchesAsync(
        SqlDataReader reader, DatasetDescription description, CancellationToken cancellationToken)
    {
        var identity = IdentityIndexes(description);
        var features = new List<Feature>(BatchSize);
        long ordinal = 0;
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = SqlServerDataStore.ReadRow(reader, reader.FieldCount);
            features.Add(SqlServerRowMapper.MapRow(description.Schema, identity, row, ordinal++, description.Srid));
        }

        return Batch(features, description);
    }

    /// <summary>Chunks the streamed features, with one empty batch when the reader produced none.</summary>
    private static FeatureBatch[] Batch(List<Feature> features, DatasetDescription description)
    {
        var schema = (FeatureSchema)description.Schema;
        return features.Count == 0
            ? [new FeatureBatch(schema, [])]
            : [.. features.Chunk(BatchSize).Select(chunk => new FeatureBatch(schema, chunk))];
    }

    private static int[] IdentityIndexes(DatasetDescription description) =>
        [.. description.IdColumns
            .Select(column => description.Schema.IndexOf(column))
            .Where(index => index >= 0)];
}
