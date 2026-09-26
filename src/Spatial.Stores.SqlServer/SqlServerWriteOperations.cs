using Microsoft.Data.SqlClient;
using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Stores.SqlServer.Core;
using Spatial.Stores.SqlServer.Data;

namespace Spatial.Stores.SqlServer;

/// <summary>
/// The write leaves of <see cref="SqlServerStore"/> (ADR-0028, ADR-0040 as
/// the SQL Server provider follows them): schema validation and the
/// per-feature insert command. They are stateless, so they live here rather
/// than on the store — the store keeps only the interface surface and its
/// connection/transaction state.
/// </summary>
internal static class SqlServerWriteOperations
{
    /// <summary>
    /// Chooses the statement and bound values for one edit, from the edited
    /// feature's own schema: the statement names exactly the columns the batch
    /// carries, so a batch that omits the identity column (the common case for
    /// an ingested dataset with a store-assigned key) is a valid add rather
    /// than a schema mismatch. An add whose identity is
    /// <see cref="FeatureId.Unassigned"/> also omits identity columns the
    /// batch does carry, so the database assigns them (ADR-0043), and returns
    /// the assigned values. An update appends the feature's pre-edit identity
    /// so the row is matched by <see cref="FeatureId"/>, never by the new
    /// attribute values. Stateless, so it lives with the write leaves rather
    /// than on the editing face (ADR-0040).
    /// </summary>
    public static (string Sql, object?[] Values) PlanFeature(
        SqlServerDatasetName name, DatasetDescription description, Feature feature, bool update) =>
        update
            ? PlanUpdate(name, description, feature)
            : PlanAdd(name, description, feature);

    private static (string Sql, object?[] Values) PlanUpdate(
        SqlServerDatasetName name, DatasetDescription description, Feature feature)
    {
        var kinds = SqlServerIdentity.Kinds(description);
        var values = SqlServerRowMapper.Parameters(feature.Schema, feature, description.Srid)
            .Concat(SqlServerDiagnostics.ParseFeatureIdentity(kinds, feature.Id))
            .ToArray();
        return (
            SqlServerQueries.Update(name, feature.Schema, description.Srid, description.IdColumns),
            values);
    }

    private static (string Sql, object?[] Values) PlanAdd(
        SqlServerDatasetName name, DatasetDescription description, Feature feature) =>
        feature.Id.Equals(FeatureId.Unassigned)
            ? PlanAddWithoutIdentity(name, description, feature)
            : (
                SqlServerQueries.InsertReturning(name, feature.Schema, description.Srid, description.IdColumns),
                SqlServerRowMapper.Parameters(feature.Schema, feature, description.Srid));

    /// <summary>
    /// The add that lets the database assign the identity: the statement names
    /// the batch's columns minus the identity ones it carried, and returns the
    /// identity the table assigned. A batch that does not carry the identity at
    /// all (the common case for an ingested dataset) is inserted as-is and
    /// still reads its identity back. A dataset with no primary key has no
    /// identity to return, so the statement is a plain insert.
    /// </summary>
    private static (string Sql, object?[] Values) PlanAddWithoutIdentity(
        SqlServerDatasetName name, DatasetDescription description, Feature feature)
    {
        var carried = description.IdColumns
            .Where(column => feature.Schema.IndexOf(column) >= 0)
            .ToArray();
        var indexes = Enumerable.Range(0, feature.Schema.Count)
            .Where(index => !carried.Contains(feature.Schema[index].Name, StringComparer.Ordinal))
            .ToArray();
        var all = SqlServerRowMapper.Parameters(feature.Schema, feature, description.Srid);
        var values = indexes.Select(index => all[index]).ToArray();
        return description.IdColumns.Count == 0
            ? (SqlServerQueries.Insert(name, feature.Schema, description.Srid), values)
            : (SqlServerQueries.InsertWithoutIdentity(name, feature.Schema, description.Srid, carried, description.IdColumns), values);
    }

    /// <summary>Rejects a batch whose schema does not describe the dataset columns.</summary>
    public static void CheckWritable(DatasetDescription description, FeatureBatch batch)
    {
        foreach (var field in batch.Schema.Fields)
        {
            RequireWritableField(description, field);
        }
    }

    private static void RequireWritableField(DatasetDescription description, FieldDefinition field)
    {
        var index = description.Schema.IndexOf(field.Name);
        if (index < 0)
        {
            throw SpatialException.BadArguments(
                $"The batch field '{field.Name}' is not a column of dataset '{description.Id}'.");
        }

        RequireMatchingKind(description, field, index);
    }

    private static void RequireMatchingKind(DatasetDescription description, FieldDefinition field, int index)
    {
        if (description.Schema[index].Kind != field.Kind)
        {
            throw SpatialException.BadArguments(
                $"The batch field '{field.Name}' is {field.Kind} but the dataset column is {description.Schema[index].Kind}.");
        }
    }

    /// <summary>Inserts every feature of a batch on the given connection/transaction and returns the row count.</summary>
    public static async Task<int> WriteOnAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        SqlServerDatasetName name,
        DatasetDescription description,
        FeatureBatch batch,
        CancellationToken token)
    {
        var sql = SqlServerQueries.Insert(name, batch.Schema, description.Srid);
        var count = 0;
        foreach (var feature in batch.Features)
        {
            count += await WriteOneAsync(
                connection, transaction, new RowWrite(sql, feature, batch.Schema, description.Srid), token);
        }

        return count;
    }

    /// <summary>One row write: the statement to run, the feature and the schema that shapes its parameters.</summary>
    private sealed record RowWrite(string Sql, Feature Feature, FeatureSchema Schema, int Srid);

    private static async Task<int> WriteOneAsync(
        SqlConnection connection, SqlTransaction transaction, RowWrite row, CancellationToken token)
    {
        var values = SqlServerRowMapper.Parameters(row.Schema, row.Feature, row.Srid);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = row.Sql;
        for (var i = 0; i < values.Length; i++)
        {
            command.Parameters.AddWithValue($"p{i}", values[i] ?? DBNull.Value);
        }

        await command.ExecuteNonQueryAsync(token);
        return 1;
    }
}
