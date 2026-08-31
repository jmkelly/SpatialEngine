using Npgsql;
using Spatial.Core.Features;
using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Providers;
using Spatial.Provider.PostGIS.Core;
using Spatial.Provider.PostGIS.Data;

namespace Spatial.Provider.PostGIS;

/// <summary>
/// The store-bound schema-discovery half of the PostGIS provider (ADR-0028):
/// reads a dataset's catalogue facts (columns, geometry columns, primary
/// keys, row estimate) and builds the <see cref="DatasetDescription"/> the
/// scan/query/write handlers need. The pure row-to-description mapping lives
/// in <see cref="PostgisSchemaDiscovery"/> (unit-tested); this class only
/// owns the Npgsql reads, exercised by the containerised integration suite.
/// </summary>
internal sealed class PostgisSchemaReader
{
    private readonly Lazy<PostgisDataStore> _store;

    public PostgisSchemaReader(Lazy<PostgisDataStore> store)
    {
        _store = store;
    }

    public async Task<DatasetDescription> DescribeAsync(
        CapabilityInvocation invocation,
        PostgisDatasetName dataset,
        CancellationToken token)
    {
        await using var connection = await _store.Value.OpenConnectionAsync(token);
        var facts = new PostgisSchemaDiscovery.SchemaFacts(
            await ReadColumnsAsync(connection, dataset, token),
            await ReadGeometriesAsync(connection, dataset, token),
            await ReadPrimaryKeysAsync(connection, dataset, token),
            await ReadRowEstimateAsync(connection, dataset, token));
        if (PostgisSchemaDiscovery.TryBuild(dataset, facts, out var description, out var reason))
        {
            return description;
        }

        throw new PostgisUnknownDatasetException($"{invocation.Capability} cannot read '{dataset}': {reason}");
    }

    /// <summary>The schema indexes of the primary-key columns (empty when the table has none).</summary>
    public static int[] IdentityIndexes(DatasetDescription description) =>
        description.IdColumns.Select(column => description.Schema.IndexOf(column))
            .Where(index => index >= 0)
            .ToArray();

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
}
