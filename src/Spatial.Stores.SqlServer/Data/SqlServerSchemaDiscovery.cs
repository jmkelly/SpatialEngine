using System.Globalization;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Stores.SqlServer.Core;

namespace Spatial.Stores.SqlServer.Data;

/// <summary>
/// Pure schema discovery (ADR-0028 as the SQL Server provider follows it):
/// turns the raw catalogue rows (column metadata, primary keys, row estimate,
/// sampled geometry facts) into a <see cref="DatasetDescription"/>. Every
/// column must map to a supported attribute kind
/// (<see cref="SqlServerTypeMapping"/>); a table without a geometry or
/// geography column is not a spatial dataset. The scan schema is the full
/// column set in column order — geometry columns included — and the primary
/// key columns (if any) are the feature-identity columns.
/// </summary>
internal static class SqlServerSchemaDiscovery
{
    /// <summary>One <c>sys.columns</c> row, in column order.</summary>
    internal readonly record struct ColumnRow(string Name, string TypeName, bool Nullable, int Ordinal);

    /// <summary>The raw catalogue facts one discovery run builds a description from.</summary>
    internal readonly record struct SchemaFacts(
        IReadOnlyList<ColumnRow> Columns,
        IReadOnlyList<string> PrimaryKeyColumns,
        long RowEstimate);

    /// <summary>What the data (or the dataset's metadata) says about the geometry column.</summary>
    internal readonly record struct GeometryFacts(int Srid, string GeometryType);

    public static bool TryBuild(
        SqlServerDatasetName dataset,
        SchemaFacts facts,
        GeometryFacts geometry,
        out DatasetDescription description,
        out string error)
    {
        description = null!;
        error = string.Empty;
        if (facts.Columns.Count == 0)
        {
            error = $"the dataset '{dataset}' has no columns (it does not exist or is not a plain table).";
            return false;
        }

        var columns = Order(facts.Columns);
        var fields = new FieldDefinition[columns.Length];
        for (var i = 0; i < columns.Length; i++)
        {
            var column = columns[i];
            if (!SqlServerTypeMapping.TryMap(column.TypeName, out var kind, out var unsupported))
            {
                error = $"the column '{column.Name}' of '{dataset}' cannot be read: {unsupported}";
                return false;
            }

            fields[i] = new FieldDefinition(column.Name, kind, column.Nullable);
        }

        var geometryColumn = GeometryColumn(facts);
        if (geometryColumn is null)
        {
            error = $"the dataset '{dataset}' is not a spatial dataset: it has no geometry or geography column.";
            return false;
        }

        description = new DatasetDescription(
            dataset.Qualified,
            dataset.Schema,
            dataset.Table,
            geometryColumn,
            geometry.Srid,
            geometry.GeometryType,
            facts.RowEstimate,
            facts.PrimaryKeyColumns,
            new FeatureSchema(fields));
        return true;
    }

    /// <summary>Builds a catalogue entry from one materialised catalogue row (pure).</summary>
    public static DatasetSummary SummaryFromRow(IReadOnlyList<object?> row, GeometryFacts geometry) =>
        new(
            $"{row[0]}.{row[1]}",
            (string)row[0]!,
            (string)row[1]!,
            (string)row[2]!,
            geometry.Srid,
            Convert.ToInt64(row[3], CultureInfo.InvariantCulture));

    /// <summary>
    /// The geometry facts a dataset's own data implies: the sampled SRID and
    /// geometry type, or the recorded/default pair when the table holds none.
    /// SRID 0 is SQL Server's planar (unknown CRS) marker, kept as-is.
    /// </summary>
    public static GeometryFacts FactsFrom(object? sampledSrid, object? sampledType, int recordedSrid) =>
        sampledSrid is null or DBNull
            ? new GeometryFacts(recordedSrid, SqlServerQueries.DefaultGeometryType)
            : new GeometryFacts(
                Convert.ToInt32(sampledSrid, CultureInfo.InvariantCulture),
                sampledType is null or DBNull
                    ? SqlServerQueries.DefaultGeometryType
                    : (string)sampledType);

    private static ColumnRow[] Order(IReadOnlyList<ColumnRow> columns) =>
        [.. columns.OrderBy(column => column.Ordinal)];

    /// <summary>
    /// The dataset's primary geometry column — the first spatial column in
    /// column order — or null when the table carries no geometry, which is
    /// what makes it not a spatial dataset.
    /// </summary>
    public static string? GeometryColumn(SchemaFacts facts)
    {
        var ordered = Order(facts.Columns);
        foreach (var column in ordered)
        {
            if (SqlServerTypeMapping.IsSpatialType(column.TypeName))
            {
                return column.Name;
            }
        }

        return null;
    }
}
