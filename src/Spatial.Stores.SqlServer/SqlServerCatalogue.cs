using Microsoft.Data.SqlClient;
using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Stores.SqlServer.Core;
using Spatial.Stores.SqlServer.Data;

namespace Spatial.Stores.SqlServer;

/// <summary>
/// The catalogue face of a <see cref="SqlServerStore"/> (ADR-0033 as the SQL
/// Server provider follows it): which datasets exist, what one of them is, and
/// creating an empty one. Listing and describing read the database catalogue
/// directly; describing turns the raw catalogue facts into a
/// <see cref="DatasetDescription"/> through the pure
/// <see cref="SqlServerSchemaDiscovery"/>.
/// <para>
/// SQL Server keeps the SRID with each value rather than on the column, so the
/// geometry facts are discovered by sampling the dataset's first non-null
/// value; an empty dataset falls back to the SRID recorded in the
/// provider's dataset metadata, and finally to the default.
/// </para>
/// </summary>
internal sealed class SqlServerCatalogue(SqlServerStorage storage)
{
    /// <summary>Every dataset, optionally filtered by a SQL <c>LIKE</c> pattern.</summary>
    public async Task<IReadOnlyList<DatasetSummary>> ListAsync(string? pattern, CancellationToken cancellationToken)
    {
        await using var connection = await storage.OpenConnectionAsync(cancellationToken);
        var parameters = pattern is null ? [] : new List<object?> { pattern };
        var rows = await SqlServerDataStore.ReadRowsAsync(
            connection, SqlServerQueries.Catalogue(pattern), parameters, cancellationToken);
        var samples = await ReadSamplesAsync(connection, rows, cancellationToken);
        return Summaries(rows, samples);
    }

    /// <summary>The discovered description of one dataset; <c>not.found</c> when it is not a spatial dataset.</summary>
    public async Task<DatasetDescription> DescribeAsync(
        SqlServerDatasetName name, CancellationToken cancellationToken)
    {
        await using var connection = await storage.OpenConnectionAsync(cancellationToken);
        var facts = await ReadSchemaFactsAsync(connection, name, cancellationToken);
        var geometry = await ReadGeometryFactsAsync(connection, name, facts, cancellationToken);
        if (SqlServerSchemaDiscovery.TryBuild(name, facts, geometry, out var description, out var reason))
        {
            return description;
        }

        throw SpatialException.Missing($"Cannot read dataset '{name}': {reason}");
    }

    /// <summary>
    /// Creates the dataset table for a sample batch — with the spatial and
    /// attribute indexes a pushed-down query needs (ADR-0092) — records its
    /// SRID, and returns its qualified name. The sample is rejected here when
    /// its schema names an unsupported field or carries a geometry SQL Server
    /// cannot store. The table, its indexes and its CRS are one transaction: a
    /// dataset whose indexes cannot be created does not exist, and a server
    /// that cannot create them reports <c>store.unavailable</c> rather than
    /// leaving a table the planner scans.
    /// </summary>
    public async Task<string> CreateAsync(
        SqlServerDatasetName name, FeatureBatch sample, int srid, CancellationToken cancellationToken)
    {
        SqlServerFieldName.RequireValid(name, sample.Schema);
        SqlServerGeometryLayout.RequireStorable(sample.Schema, sample.Features);
        await using var connection = await storage.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await SqlServerDataStore.ExecuteNonQueryAsync(
            connection, transaction, SqlServerQueries.CreateTable(name, sample.Schema), [], cancellationToken);
        await CreateIndexesAsync(connection, transaction, name, sample.Schema, cancellationToken);
        await RecordSridAsync(connection, transaction, name, srid, cancellationToken);
        // In the same transaction as the table: a dataset that was created
        // carries a version of its own, so a cache never serves a tile drawn
        // before the dataset existed (ADR-0129).
        await SqlServerContentVersions.BumpAsync(connection, transaction, name, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return name.Qualified;
    }

    /// <summary>Runs the dataset's index statements, unless index creation is switched off (ADR-0092).</summary>
    private async Task CreateIndexesAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        SqlServerDatasetName name,
        IFeatureSchema schema,
        CancellationToken cancellationToken)
    {
        foreach (var statement in storage.CreateIndexes
            ? SqlServerIndexPlan.CreateIndexes(name, schema)
            : [])
        {
            await SqlServerDataStore.ExecuteNonQueryAsync(connection, transaction, statement, [], cancellationToken);
        }
    }

    /// <summary>
    /// Records the SRID a dataset was created with, creating the metadata
    /// sidecar on first use. The dataset and its CRS become visible together.
    /// </summary>
    internal static async Task RecordSridAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        SqlServerDatasetName name,
        int srid,
        CancellationToken cancellationToken)
    {
        await SqlServerDataStore.ExecuteNonQueryAsync(
            connection, transaction, SqlServerQueries.EnsureDatasetMetadataTable(), [], cancellationToken);
        await SqlServerDataStore.ExecuteNonQueryAsync(
            connection, transaction, SqlServerQueries.UpsertDatasetSrid(), [name.Qualified, srid], cancellationToken);
    }

    private static async Task<SqlServerSchemaDiscovery.SchemaFacts> ReadSchemaFactsAsync(
        SqlConnection connection, SqlServerDatasetName name, CancellationToken cancellationToken)
    {
        var parameters = new List<object?> { name.Schema, name.Table };
        var columns = await SqlServerDataStore.ReadRowsAsync(
            connection, SqlServerQueries.ColumnsMetadata(), parameters, cancellationToken);
        var keys = await SqlServerDataStore.ReadRowsAsync(
            connection, SqlServerQueries.PrimaryKeyColumns(), parameters, cancellationToken);
        var estimates = await SqlServerDataStore.ReadRowsAsync(
            connection, SqlServerQueries.RowEstimate(), parameters, cancellationToken);
        var schema = new SqlServerSchemaDiscovery.SchemaFacts(
            columns.Select(ColumnRow).ToArray(),
            keys.Select(row => (string)row[0]!).ToArray(),
            estimates.Count > 0 ? Convert.ToInt64(estimates[0][0], System.Globalization.CultureInfo.InvariantCulture) : 0);
        return schema;
    }

    private static SqlServerSchemaDiscovery.ColumnRow ColumnRow(IReadOnlyList<object?> row) =>
        new(
            (string)row[0]!,
            (string)row[1]!,
            Convert.ToBoolean(row[2], System.Globalization.CultureInfo.InvariantCulture),
            Convert.ToInt32(row[3], System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>
    /// The geometry facts of one dataset: the sampled SRID and geometry type,
    /// then the recorded SRID (and the default) when the dataset holds none.
    /// </summary>
    private static async Task<SqlServerSchemaDiscovery.GeometryFacts> ReadGeometryFactsAsync(
        SqlConnection connection,
        SqlServerDatasetName name,
        SqlServerSchemaDiscovery.SchemaFacts facts,
        CancellationToken cancellationToken)
    {
        var geometryColumn = SqlServerSchemaDiscovery.GeometryColumn(facts);
        var recorded = await ReadRecordedSridAsync(connection, name, cancellationToken);
        if (geometryColumn is null)
        {
            return new SqlServerSchemaDiscovery.GeometryFacts(recorded, SqlServerQueries.DefaultGeometryType);
        }

        var sample = await SqlServerDataStore.ReadRowsAsync(
            connection, SqlServerQueries.SampleGeometry(name, geometryColumn), [], cancellationToken);
        return sample.Count == 0
            ? new SqlServerSchemaDiscovery.GeometryFacts(recorded, SqlServerQueries.DefaultGeometryType)
            : SqlServerSchemaDiscovery.FactsFrom(sample[0][0], sample[0][1], recorded);
    }

    private static async Task<int> ReadRecordedSridAsync(
        SqlConnection connection, SqlServerDatasetName name, CancellationToken cancellationToken)
    {
        var rows = await SqlServerDataStore.ReadRowsAsync(
            connection, SqlServerQueries.DatasetSrid(), [name.Qualified], cancellationToken);
        return rows.Count > 0
            ? Convert.ToInt32(rows[0][0], System.Globalization.CultureInfo.InvariantCulture)
            : SqlServerQueries.DefaultSrid;
    }

    /// <summary>
    /// Samples every listed dataset's geometry facts in one statement, so the
    /// catalogue costs two round trips regardless of how many datasets there
    /// are. The sampled rows come back in catalogue order.
    /// </summary>
    private static async Task<IReadOnlyList<SqlServerSchemaDiscovery.GeometryFacts>> ReadSamplesAsync(
        SqlConnection connection,
        IReadOnlyList<IReadOnlyList<object?>> rows,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        var samples = rows.Select(GeometrySample).ToArray();
        var sampled = await SqlServerDataStore.ReadRowsAsync(
            connection, SqlServerQueries.SampleAllGeometry(samples), [], cancellationToken);
        return sampled
            .Select(row => SqlServerSchemaDiscovery.FactsFrom(row[1], row[2], SqlServerQueries.DefaultSrid))
            .ToArray();
    }

    private static DatasetSummary[] Summaries(
        IReadOnlyList<IReadOnlyList<object?>> rows,
        IReadOnlyList<SqlServerSchemaDiscovery.GeometryFacts> samples) =>
        [.. rows.Select((row, index) =>
            SqlServerSchemaDiscovery.SummaryFromRow(row, samples.Count > index ? samples[index] : DefaultFacts()))];

    private static SqlServerSchemaDiscovery.GeometryFacts DefaultFacts() =>
        new(SqlServerQueries.DefaultSrid, SqlServerQueries.DefaultGeometryType);

    private static GeometrySample GeometrySample(IReadOnlyList<object?> row) =>
        new(DatasetNameOf(row), (string)row[2]!);

    /// <summary>
    /// The dataset identifier of a catalogue row. The name comes from the
    /// catalogue itself, so it is validated rather than assumed: an
    /// unparseable row cannot reach generated T-SQL.
    /// </summary>
    private static SqlServerDatasetName DatasetNameOf(IReadOnlyList<object?> row)
    {
        var text = $"{row[0]}.{row[1]}";
        return SqlServerDatasetName.TryParse(text, out var dataset, out var reason)
            ? dataset
            : throw new InvalidOperationException($"the catalogue row names '{text}', which is not a dataset identifier: {reason}.");
    }
}
