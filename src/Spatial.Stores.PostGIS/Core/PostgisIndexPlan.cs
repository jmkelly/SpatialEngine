using Spatial.Core.Features;
using Spatial.Stores.PostGIS.Data;

namespace Spatial.Stores.PostGIS.Core;

/// <summary>
/// The indexes a created dataset carries (ADR-0081): a GiST index on the
/// dataset's primary geometry column — the one the bounding-box pushdown
/// filters (<see cref="PostgisFilterSql.AppendBoundingBox"/>) — and a btree
/// index on every attribute column, because a pushed-down attribute filter may
/// name any field of the schema and an unindexed one is a sequential scan.
/// Columns the create already keys with a primary key are left alone: a second
/// index on the same column is cost without benefit.
/// <para>
/// Pure planning, free of Npgsql: the statements are deterministic for equal
/// inputs and every identifier is quoted from a validated dataset name and a
/// schema-validated field name.
/// </para>
/// </summary>
internal static class PostgisIndexPlan
{
    /// <summary>
    /// The <c>CREATE INDEX</c> statements for a dataset, in creation order.
    /// <paramref name="keyedColumns"/> are the columns the create statement
    /// already carries a primary key on.
    /// </summary>
    public static IReadOnlyList<string> CreateIndexes(
        PostgisDatasetName dataset, IFeatureSchema schema, IReadOnlyList<string>? keyedColumns = null)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var keyed = keyedColumns ?? [];
        var statements = new List<string>();
        var geometry = PrimaryGeometryColumn(schema);
        if (geometry is not null)
        {
            statements.Add(PostgisQueries.CreateSpatialIndex(dataset, geometry));
        }

        statements.AddRange(AttributeColumns(schema, keyed).Select(column => PostgisQueries.CreateBtreeIndex(dataset, column)));
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

    private static IEnumerable<string> AttributeColumns(IFeatureSchema schema, IReadOnlyList<string> keyed)
    {
        for (var i = 0; i < schema.Count; i++)
        {
            var field = schema[i];
            if (field.Kind != AttributeKind.Geometry && !keyed.Contains(field.Name, StringComparer.Ordinal))
            {
                yield return field.Name;
            }
        }
    }
}
