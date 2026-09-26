using Npgsql;
using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Stores.PostGIS.Core;
using Spatial.Stores.PostGIS.Data;
using CoreBoundingBox = Spatial.Contracts.BoundingBox;

namespace Spatial.Stores.PostGIS;

/// <summary>
/// The catalogue face of a <see cref="PostgisStore"/> (ADR-0033): which
/// datasets exist, what one of them is, and creating an empty one. Listing and
/// describing read the database catalogue directly; describing turns the raw
/// catalogue facts into a <see cref="DatasetDescription"/> through the pure
/// <see cref="PostgisSchemaDiscovery"/>.
/// </summary>
internal sealed class PostgisCatalogue(PostgisStorage storage)
{
    /// <summary>Every dataset, optionally filtered by a SQL <c>LIKE</c> pattern.</summary>
    public async Task<IReadOnlyList<DatasetSummary>> ListAsync(string? pattern, CancellationToken cancellationToken)
    {
        await using var connection = await storage.OpenConnectionAsync(cancellationToken);
        var parameters = pattern is null ? [] : new List<object?> { pattern };
        var rows = await PostgisDataStore.ReadRowsAsync(connection, PostgisQueries.Catalogue(pattern), parameters, cancellationToken);
        return rows.Select(PostgisSchemaDiscovery.SummaryFromRow).ToArray();
    }

    /// <summary>The discovered description of one dataset; <c>not.found</c> when it is not a spatial dataset.</summary>
    public async Task<DatasetDescription> DescribeAsync(PostgisDatasetName name, CancellationToken cancellationToken)
    {
        await using var connection = await storage.OpenConnectionAsync(cancellationToken);
        var facts = await ReadSchemaFactsAsync(connection, name, cancellationToken);
        if (PostgisSchemaDiscovery.TryBuild(name, facts, out var description, out var reason))
        {
            return description;
        }

        throw SpatialException.Missing($"Cannot read dataset '{name}': {reason}");
    }

    /// <summary>
    /// Creates the dataset table for a sample batch, and returns its qualified
    /// name. The sample is rejected here when its schema names an unsupported
    /// field or leaves the dataset with no geometry.
    /// </summary>
    public async Task<string> CreateAsync(
        PostgisDatasetName name, FeatureBatch sample, int srid, CancellationToken cancellationToken)
    {
        PostgisFieldName.RequireValid(name, sample.Schema);
        var geometryTypes = CreatableGeometryTypes(name, sample);
        await using var connection = await storage.OpenConnectionAsync(cancellationToken);
        await PostgisDataStore.ExecuteNonQueryAsync(
            connection, PostgisQueries.CreateTable(name, sample.Schema, srid, geometryTypes), [], cancellationToken);
        return name.Qualified;
    }

    /// <summary>Rejects a sample whose schema the dataset cannot be created from, and returns its resolved geometry types.</summary>
    private static string[] CreatableGeometryTypes(PostgisDatasetName name, FeatureBatch sample)
    {
        if (PostgisGeometryType.TryResolve(sample.Schema, sample.Features, out var geometryTypes, out var geometryError))
        {
            return geometryTypes;
        }

        throw SpatialException.BadArguments($"The dataset '{name}' cannot be created: {geometryError}");
    }

    private static async Task<PostgisSchemaDiscovery.SchemaFacts> ReadSchemaFactsAsync(
        NpgsqlConnection connection, PostgisDatasetName name, CancellationToken cancellationToken)
    {
        var parameters = new List<object?> { name.Schema, name.Table };
        var columns = await PostgisDataStore.ReadRowsAsync(connection, PostgisQueries.ColumnsMetadata(), parameters, cancellationToken);
        var geometries = await PostgisDataStore.ReadRowsAsync(connection, PostgisQueries.GeometryColumnsMetadata(), parameters, cancellationToken);
        var keys = await PostgisDataStore.ReadRowsAsync(connection, PostgisQueries.PrimaryKeyColumns(), parameters, cancellationToken);
        var estimateRows = await PostgisDataStore.ReadRowsAsync(connection, PostgisQueries.RowEstimate(), parameters, cancellationToken);
        return new PostgisSchemaDiscovery.SchemaFacts(
            columns.Select(ColumnRow).ToArray(),
            geometries.Select(row => new PostgisSchemaDiscovery.GeometryRow((string)row[0]!, (int)row[1]!, (string)row[2]!)).ToArray(),
            keys.Select(row => (string)row[0]!).ToArray(),
            RowEstimate(estimateRows));
    }

    private static PostgisSchemaDiscovery.ColumnRow ColumnRow(IReadOnlyList<object?> row) =>
        new(
            (string)row[0]!,
            (string)row[1]!,
            string.Equals((string)row[2]!, "YES", StringComparison.Ordinal),
            (int)row[3]!);

    private static long RowEstimate(IReadOnlyList<IReadOnlyList<object?>> rows) =>
        rows.Count > 0
            ? Convert.ToInt64(rows[0][0], System.Globalization.CultureInfo.InvariantCulture)
            : 0;
}
