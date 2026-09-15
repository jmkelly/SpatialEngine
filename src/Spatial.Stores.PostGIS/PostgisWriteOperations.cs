using Npgsql;
using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Stores.PostGIS.Core;
using Spatial.Stores.PostGIS.Data;

namespace Spatial.Stores.PostGIS;

/// <summary>
/// The write leaves of <see cref="PostgisStore"/> (ADR-0028, ADR-0040):
/// schema validation and the per-feature insert command. They are stateless,
/// so they live here rather than on the store — the store keeps only the
/// interface surface and its connection/transaction state.
/// </summary>
internal static class PostgisWriteOperations
{
    /// <summary>
    /// Chooses the statement and bound values for one edit. An add whose
    /// identity is <see cref="FeatureId.Unassigned"/> omits the identity
    /// columns so the database assigns them (ADR-0043); every other add and
    /// update binds the full schema, and an update appends the feature's
    /// pre-edit identity so the row is matched by <see cref="FeatureId"/>.
    /// Stateless, so it lives with the write
    /// leaves rather than on the editing face (ADR-0040).
    /// </summary>
    public static (string Sql, object?[] Values) PlanFeature(
        PostgisDatasetName name, DatasetDescription description, Feature feature, bool update)
    {
        if (update)
        {
            var kinds = description.IdColumns
                .Select(column => description.Schema[description.Schema.IndexOf(column)].Kind)
                .ToArray();
            var values = PostgisRowMapper.Parameters(description.Schema, feature, description.Srid)
                .Concat(PostgisDiagnostics.ParseFeatureIdentity(kinds, feature.Id))
                .ToArray();
            return (
                PostgisQueries.Update(name, description.Schema, description.Srid, description.IdColumns),
                values);
        }

        if (!feature.Id.Equals(FeatureId.Unassigned))
        {
            return (
                PostgisQueries.InsertReturning(name, description.Schema, description.Srid, description.IdColumns),
                PostgisRowMapper.Parameters(description.Schema, feature, description.Srid));
        }

        var indexes = Enumerable.Range(0, description.Schema.Count)
            .Where(index => !description.IdColumns.Contains(description.Schema[index].Name, StringComparer.Ordinal))
            .ToArray();
        var all = PostgisRowMapper.Parameters(description.Schema, feature, description.Srid);
        return (
            PostgisQueries.InsertWithoutIdentity(name, description.Schema, description.Srid, description.IdColumns),
            indexes.Select(index => all[index]).ToArray());
    }

    /// <summary>Rejects a batch whose schema does not describe the dataset columns.</summary>
    public static void CheckWritable(DatasetDescription description, FeatureBatch batch)
    {
        foreach (var field in batch.Schema.Fields)
        {
            var index = description.Schema.IndexOf(field.Name);
            if (index < 0)
            {
                throw SpatialException.BadArguments(
                    $"The batch field '{field.Name}' is not a column of dataset '{description.Id}'.");
            }

            if (description.Schema[index].Kind != field.Kind)
            {
                throw SpatialException.BadArguments(
                    $"The batch field '{field.Name}' is {field.Kind} but the dataset column is {description.Schema[index].Kind}.");
            }
        }
    }

    /// <summary>Inserts every feature of a batch on the given connection/transaction and returns the row count.</summary>
    public static async Task<int> WriteOnAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, PostgisDatasetName name,
        DatasetDescription description, FeatureBatch batch, CancellationToken token)
    {
        var sql = PostgisQueries.Insert(name, batch.Schema, description.Srid);
        var count = 0;
        foreach (var feature in batch.Features)
        {
            var values = PostgisRowMapper.Parameters(batch.Schema, feature, description.Srid);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            for (var i = 0; i < values.Length; i++)
            {
                command.Parameters.AddWithValue($"p{i}", values[i] ?? DBNull.Value);
            }

            await command.ExecuteNonQueryAsync(token);
            count++;
        }

        return count;
    }
}
