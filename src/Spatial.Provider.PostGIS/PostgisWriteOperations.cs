using Npgsql;
using Spatial.Core.Features;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Providers;
using Spatial.Provider.PostGIS.Core;
using Spatial.Provider.PostGIS.Data;

namespace Spatial.Provider.PostGIS;

/// <summary>
/// The write leaves of <see cref="PostgisStore"/> (ADR-0028, ADR-0040):
/// schema validation and the per-feature insert command. They are stateless,
/// so they live here rather than on the store — the store keeps only the
/// interface surface and its connection/transaction state.
/// </summary>
internal static class PostgisWriteOperations
{
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
