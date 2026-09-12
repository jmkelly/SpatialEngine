using System.Text;
using Spatial.Core.Features;
using Spatial.Provider.PostGIS.Core;

namespace Spatial.Provider.PostGIS.Data;

/// <summary>
/// Every SQL statement the adapter runs (ADR-0028). Statements are built from
/// validated dataset identifiers and *discovered* column names only — never
/// from client text — so the only free text that can reach SQL is inside
/// bound parameters. The geometry column of a bounding box is quoted from
/// the dataset description (a discovered identifier); the SRID is an
/// integer from discovery. Deterministic: equal inputs produce equal text.
/// </summary>
internal static class PostgisQueries
{
    /// <summary>Full scan: every discovered field, in column order (geometry fields read as EWKB bytes).</summary>
    public static string Select(PostgisDatasetName dataset, IFeatureSchema schema) =>
        $"SELECT {SelectColumns(schema)} FROM {dataset.QuoteQualified()}";

    /// <summary>Query: the scan plus an optional <c>WHERE</c> predicate.</summary>
    public static string Query(PostgisDatasetName dataset, IFeatureSchema schema, string? predicate) =>
        predicate is null
            ? Select(dataset, schema)
            : $"SELECT {SelectColumns(schema)} FROM {dataset.QuoteQualified()} WHERE {predicate}";

    /// <summary>Insert for one feature of a writing batch (one bound parameter per field; geometry via EWKB, whose SRID is embedded).</summary>
    public static string Insert(PostgisDatasetName dataset, IFeatureSchema batchSchema)
    {
        var columns = string.Join(", ", batchSchema.Fields.Select(field => $"\"{field.Name}\""));
        var values = string.Join(", ", batchSchema.Fields.Select((field, i) =>
            field.Kind == AttributeKind.Geometry ? $"ST_GeomFromEWKB(@p{i})" : $"@p{i}"));
        return $"INSERT INTO {dataset.QuoteQualified()} ({columns}) VALUES ({values})";
    }

    /// <summary>Creates the result table from a defining batch's schema.</summary>
    public static string CreateTable(PostgisDatasetName dataset, IFeatureSchema schema, int srid)
    {
        var builder = new StringBuilder($"CREATE TABLE {dataset.QuoteQualified()} (");
        for (var i = 0; i < schema.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(", ");
            }

            var field = schema[i];
            if (field.Kind == AttributeKind.Geometry)
            {
                builder.Append('"').Append(field.Name).Append("\" geometry(Geometry, ").Append(srid).Append(')');
            }
            else
            {
                builder.Append('"').Append(field.Name).Append("\" ").Append(PostgisTypeMapping.SqlType(field.Kind));
            }
        }

        return builder.Append(')').ToString();
    }

    /// <summary>Lists every spatial table (schema, table, geometry column, srid, type, row estimate).</summary>
    public static string Catalogue(string? pattern)
    {
        var filter = pattern is null
            ? string.Empty
            : " AND (n.nspname || '.' || c.relname) LIKE @p0";
        return "SELECT n.nspname, c.relname, gc.f_geometry_column, gc.srid, gc.type, c.reltuples "
            + "FROM pg_class c "
            + "JOIN pg_namespace n ON n.oid = c.relnamespace "
            + "JOIN geometry_columns gc ON gc.f_table_schema = n.nspname AND gc.f_table_name = c.relname "
            + "WHERE c.relkind = 'r'"
            + filter
            + " ORDER BY n.nspname, c.relname";
    }

    /// <summary>Column metadata for one table (name, udt, nullability, ordinal, data type).</summary>
    public static string ColumnsMetadata() =>
        "SELECT column_name, udt_name, is_nullable, ordinal_position, data_type "
        + "FROM information_schema.columns "
        + "WHERE table_schema = @p0 AND table_name = @p1 "
        + "ORDER BY ordinal_position";

    /// <summary>Geometry column metadata for one table (column, srid, geometry type).</summary>
    public static string GeometryColumnsMetadata() =>
        "SELECT f_geometry_column, srid, type "
        + "FROM geometry_columns "
        + "WHERE f_table_schema = @p0 AND f_table_name = @p1";

    /// <summary>Primary-key column names for one table, in key order.</summary>
    public static string PrimaryKeyColumns() =>
        "SELECT kcu.column_name "
        + "FROM information_schema.table_constraints tc "
        + "JOIN information_schema.key_column_usage kcu "
        + "  ON kcu.constraint_name = tc.constraint_name AND kcu.table_schema = tc.table_schema AND kcu.table_name = tc.table_name "
        + "WHERE tc.constraint_type = 'PRIMARY KEY' AND tc.table_schema = @p0 AND tc.table_name = @p1 "
        + "ORDER BY kcu.ordinal_position";

    /// <summary>The row-count estimate from the planner statistics.</summary>
    public static string RowEstimate() =>
        "SELECT c.reltuples FROM pg_class c "
        + "JOIN pg_namespace n ON n.oid = c.relnamespace "
        + "WHERE n.nspname = @p0 AND c.relname = @p1";

    /// <summary>Whether a table exists at all (drives the 'unknown dataset' diagnostic).</summary>
    public static string TableExists() =>
        "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = @p0 AND table_name = @p1)";

    /// <summary>Scan/query columns: plain names, geometry fields wrapped so PostGIS returns canonical EWKB bytes.</summary>
    private static string SelectColumns(IFeatureSchema schema) =>
        string.Join(", ", schema.Fields.Select(field =>
            field.Kind == AttributeKind.Geometry ? $"ST_AsEWKB(\"{field.Name}\")" : $"\"{field.Name}\""));
}
