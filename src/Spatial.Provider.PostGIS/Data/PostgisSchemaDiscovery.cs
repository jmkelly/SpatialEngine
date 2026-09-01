using Spatial.Core.Features;
using Spatial.PluginSdk.Providers;
using Spatial.Provider.PostGIS.Core;

namespace Spatial.Provider.PostGIS.Data;

/// <summary>
/// Pure schema discovery (ADR-0028, architecture/distilled/contracts.md): turns the
/// raw catalogue rows (column metadata, geometry columns, primary keys, row
/// estimate) into a <see cref="DatasetDescription"/>. Every column must map
/// to a supported attribute kind (PostgisTypeMapping); a table without a
/// geometry column is not a spatial dataset. The scan schema is the full
/// column set in column order — geometry columns included — and the primary
/// key columns (if any) are the feature-identity columns.
/// </summary>
internal static class PostgisSchemaDiscovery
{
    /// <summary>One <c>information_schema.columns</c> row, in column order.</summary>
    internal readonly record struct ColumnRow(string Name, string UdtName, bool Nullable, int Ordinal);

    /// <summary>One <c>geometry_columns</c> row.</summary>
    internal readonly record struct GeometryRow(string Column, int Srid, string Type);

    /// <summary>The raw catalogue facts one discovery run builds a description from.</summary>
    internal readonly record struct SchemaFacts(
        IReadOnlyList<ColumnRow> Columns,
        IReadOnlyList<GeometryRow> GeometryColumns,
        IReadOnlyList<string> PrimaryKeyColumns,
        long RowEstimate);

    public static bool TryBuild(
        PostgisDatasetName dataset,
        SchemaFacts facts,
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

        var orderedColumns = facts.Columns.OrderBy(c => c.Ordinal).ToArray();
        var orderedGeometries = OrderByColumns(facts.GeometryColumns, orderedColumns);
        var geometryNames = new HashSet<string>(orderedGeometries.Select(g => g.Column), StringComparer.Ordinal);
        var geometryPrimary = orderedGeometries.Length > 0 ? orderedGeometries[0] : default;
        var fields = new FieldDefinition[orderedColumns.Length];
        for (var i = 0; i < orderedColumns.Length; i++)
        {
            var column = orderedColumns[i];
            if (!PostgisTypeMapping.TryMap(column.UdtName, out var kind, out var unsupported))
            {
                error = $"the column '{column.Name}' of '{dataset}' cannot be read: {unsupported}";
                return false;
            }

            if (geometryNames.Contains(column.Name) && kind != AttributeKind.Geometry)
            {
                // geometry_columns can report a view column that information_schema
                // types differently; trust the information_schema kind unless the
                // geometry view names it.
                kind = AttributeKind.Geometry;
            }

            fields[i] = new FieldDefinition(column.Name, kind, column.Nullable);
        }

        if (geometryPrimary.Column is null)
        {
            error = $"the dataset '{dataset}' is not a spatial dataset: it has no geometry column (geometry_columns).";
            return false;
        }

        description = new DatasetDescription(
            dataset.Qualified,
            dataset.Schema,
            dataset.Table,
            geometryPrimary.Column,
            geometryPrimary.Srid,
            geometryPrimary.Type,
            facts.RowEstimate,
            facts.PrimaryKeyColumns,
            new FeatureSchema(fields));
        return true;
    }

    /// <summary>Builds a catalogue entry from one materialised catalogue row (pure).</summary>
    public static DatasetSummary SummaryFromRow(IReadOnlyList<object?> row) =>
        new($"{row[0]}.{row[1]}", (string)row[0]!, (string)row[1]!, (string)row[2]!, (int)row[3]!, Convert.ToInt64(row[5], System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>Orders geometry columns by their position in the table's column list (deterministic primary geometry).</summary>
    private static GeometryRow[] OrderByColumns(
        IReadOnlyList<GeometryRow> geometryColumns,
        ColumnRow[] columns)
    {
        var ordinal = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < columns.Length; i++)
        {
            ordinal[columns[i].Name] = i;
        }

        return geometryColumns
            .OrderBy(g => ordinal.TryGetValue(g.Column, out var position) ? position : int.MaxValue)
            .ToArray();
    }
}
