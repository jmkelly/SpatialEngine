using Spatial.Core.Features;
using Spatial.Stores.SqlServer.Data;

namespace Spatial.Stores.SqlServer.Core;

/// <summary>
/// The indexes a created dataset carries (ADR-0092): a spatial index on the dataset's primary geometry column — the
/// one the bounding-box pushdown filters
/// (<see cref="SqlServerFilterSql.SqlBuilder.AppendBoundingBox"/>) — and a
/// btree index on every attribute column a pushed-down filter may name,
/// because an unindexed one is a scan. Columns the create already keys with a
/// primary key are left alone: a second index on the same column is cost
/// without benefit.
/// <para>
/// Two SQL Server facts shape what the plan can ask for, both verified against
/// the container the tests run (ADR-0092):
/// </para>
/// <list type="bullet">
/// <item>a spatial index requires the table to have a **clustered primary
/// key**, so a dataset created without one carries no spatial index — the
/// plan is told so and says why;</item>
/// <item>a <c>nvarchar(max)</c> column cannot be an index key, so a text
/// column is not offered a btree
/// (<see cref="SqlServerTypeMapping.IsIndexableKind"/>).</item>
/// </list>
/// <para>
/// Pure planning, free of SqlClient: the statements are deterministic for
/// equal inputs and every identifier is bracket-quoted from a validated dataset
/// name and a schema-validated field name.
/// </para>
/// </summary>
internal static class SqlServerIndexPlan
{
    /// <summary>
    /// The index-creation statements for a dataset, in creation order.
    /// <paramref name="keyedColumns"/> are the columns the create statement
    /// already carries a primary key on;
    /// <paramref name="clusteredPrimaryKey"/> says whether that primary key is
    /// the clustered one SQL Server demands before it will grid a table.
    /// </summary>
    public static IReadOnlyList<string> CreateIndexes(
        SqlServerDatasetName dataset,
        IFeatureSchema schema,
        IReadOnlyList<string>? keyedColumns = null,
        bool clusteredPrimaryKey = false)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var keyed = keyedColumns ?? [];
        var statements = new List<string>();
        var geometry = PrimaryGeometryColumn(schema);
        if (geometry is not null && clusteredPrimaryKey)
        {
            statements.Add(SqlServerQueries.CreateSpatialIndex(dataset, geometry, GridType()));
        }

        statements.AddRange(AttributeColumns(schema, keyed).Select(column => SqlServerQueries.CreateBtreeIndex(dataset, column)));
        return statements;
    }

    /// <summary>
    /// The primary geometry column of a creating schema — the first geometry
    /// field in column order, which is the rule schema discovery applies to a
    /// table read back from the database.
    /// </summary>
    public static string? PrimaryGeometryColumn(IFeatureSchema schema)
    {
        for (var i = 0; i < schema.Count; i++)
        {
            if (schema[i].Kind == AttributeKind.Geometry)
            {
                return schema[i].Name;
            }
        }

        return null;
    }

    /// <summary>
    /// The grid the spatial index takes, derived from the T-SQL type the create
    /// statement gives a geometry column — so the two can never disagree about
    /// which kind of grid the column takes.
    /// </summary>
    private static string GridType() =>
        SqlServerTypeMapping.SpatialGridType(SqlServerTypeMapping.SqlType(AttributeKind.Geometry));

    private static IEnumerable<string> AttributeColumns(IFeatureSchema schema, IReadOnlyList<string> keyed)
    {
        for (var i = 0; i < schema.Count; i++)
        {
            var field = schema[i];
            if (field.Kind != AttributeKind.Geometry
                && SqlServerTypeMapping.IsIndexableKind(field.Kind)
                && !keyed.Contains(field.Name, StringComparer.Ordinal))
            {
                yield return field.Name;
            }
        }
    }
}
