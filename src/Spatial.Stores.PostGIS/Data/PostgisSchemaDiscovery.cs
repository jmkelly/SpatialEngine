using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.Stores.PostGIS.Core;

namespace Spatial.Stores.PostGIS.Data;

/// <summary>
/// Pure schema discovery (ADR-0028, architecture/distilled/contracts.md): turns the
/// raw catalogue rows (column metadata, geometry columns, declared type
/// modifiers, primary keys, row estimate) into a <see cref="DatasetDescription"/>.
/// Every column must map
/// to a supported attribute kind (PostgisTypeMapping); a table without a
/// geometry column is not a spatial dataset. The scan schema is the full
/// column set in column order — geometry columns included — and the primary
/// key columns (if any) are the feature-identity columns. The geometry
/// column's declared Z/M comes from its type modifier (ADR-0084).
/// </summary>
internal static class PostgisSchemaDiscovery
{
    /// <summary>One <c>information_schema.columns</c> row, in column order.</summary>
    internal readonly record struct ColumnRow(string Name, string UdtName, bool Nullable, int Ordinal);

    /// <summary>One <c>geometry_columns</c> row.</summary>
    internal readonly record struct GeometryRow(string Column, int Srid, string Type);

    /// <summary>
    /// One column's declared type modifier — the formatted PostGIS type
    /// PostgreSQL renders for it (<c>geometry(PointZ,4326)</c>). It is the
    /// only proof the store has of a geometry column's Z/M (ADR-0084).
    /// </summary>
    internal readonly record struct TypeModifierRow(string Column, string? TypeModifier);

    /// <summary>The raw catalogue facts one discovery run builds a description from.</summary>
    internal readonly record struct SchemaFacts(
        IReadOnlyList<ColumnRow> Columns,
        IReadOnlyList<GeometryRow> GeometryColumns,
        IReadOnlyList<TypeModifierRow> TypeModifiers,
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
            new FeatureSchema(fields),
            DeclaredLayout(facts, geometryPrimary.Column));
        return true;
    }

    /// <summary>
    /// The ordinates the primary geometry column <em>declares</em> (ADR-0084):
    /// its formatted type modifier parsed for the Z/M type-name suffix. A
    /// column with no modifier, or one whose modifier does not parse, is
    /// reported two-dimensional — the engine then serves whatever Z/M the
    /// values happen to carry without claiming the dataset does.
    /// </summary>
    private static CoordinateLayout DeclaredLayout(SchemaFacts facts, string column)
    {
        var modifier = facts.TypeModifiers.FirstOrDefault(
            candidate => string.Equals(candidate.Column, column, StringComparison.Ordinal)).TypeModifier;
        return PostgisCoordinateLayout.FromTypeModifier(modifier);
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
