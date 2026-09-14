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

    /// <summary>
    /// Reads features by identity (ADR-0038): one OR-group per requested
    /// identity tuple, one bound parameter per identity value, in input order.
    /// A single tuple carries <c>LIMIT 1</c> (the primary-key predicate is
    /// already unique); a batch has no limit because each tuple matches at
    /// most one row. Both the identity columns and the dataset identifier are
    /// discovered identifiers, never client text.
    /// </summary>
    public static string SelectByIdentity(
        PostgisDatasetName dataset, IFeatureSchema schema, IReadOnlyList<string> identityColumns, int count)
    {
        var builder = new StringBuilder($"SELECT {SelectColumns(schema)} FROM {dataset.QuoteQualified()} WHERE ");
        for (var feature = 0; feature < count; feature++)
        {
            if (feature > 0)
            {
                builder.Append(" OR ");
            }

            builder.Append('(');
            for (var column = 0; column < identityColumns.Count; column++)
            {
                if (column > 0)
                {
                    builder.Append(" AND ");
                }

                builder.Append('"').Append(identityColumns[column]).Append("\" = @p").Append((feature * identityColumns.Count) + column);
            }

            builder.Append(')');
        }

        return count == 1 ? builder.Append(" LIMIT 1").ToString() : builder.ToString();
    }

    /// <summary>Insert for one feature of a writing batch (one bound parameter per field; geometry via EWKB with the column SRID enforced).</summary>
    public static string Insert(PostgisDatasetName dataset, IFeatureSchema batchSchema, int srid)
    {
        var columns = string.Join(", ", batchSchema.Fields.Select(field => $"\"{field.Name}\""));
        var values = string.Join(", ", batchSchema.Fields.Select((field, i) =>
            field.Kind == AttributeKind.Geometry ? $"ST_SetSRID(ST_GeomFromEWKB(@p{i}), {srid})" : $"@p{i}"));
        return $"INSERT INTO {dataset.QuoteQualified()} ({columns}) VALUES ({values})";
    }

    /// <summary>Insert that returns the store-assigned identity columns (ADR-0037); unchanged when the table has no identity.</summary>
    public static string InsertReturning(PostgisDatasetName dataset, IFeatureSchema batchSchema, int srid, IReadOnlyList<string> identityColumns)
    {
        var insert = Insert(dataset, batchSchema, srid);
        return identityColumns.Count == 0
            ? insert
            : insert + " RETURNING " + string.Join(", ", identityColumns.Select(column => $"\"{column}\""));
    }

    /// <summary>
    /// Insert that omits the identity columns so the database assigns them
    /// (ADR-0043), returning the assigned values. Used when the client did not
    /// supply an <c>OBJECTID</c>.
    /// </summary>
    public static string InsertWithoutIdentity(
        PostgisDatasetName dataset, IFeatureSchema schema, int srid, IReadOnlyList<string> identityColumns)
    {
        var columns = schema.Fields.Where(field => !identityColumns.Contains(field.Name, StringComparer.Ordinal)).ToArray();
        var names = string.Join(", ", columns.Select(field => $"\"{field.Name}\""));
        var values = string.Join(", ", columns.Select((field, i) =>
            field.Kind == AttributeKind.Geometry ? $"ST_SetSRID(ST_GeomFromEWKB(@p{i}), {srid})" : $"@p{i}"));
        var returning = string.Join(", ", identityColumns.Select(column => $"\"{column}\""));
        return $"INSERT INTO {dataset.QuoteQualified()} ({names}) VALUES ({values}) RETURNING {returning}";
    }

    /// <summary>
    /// Updates one feature in place. The identity predicate binds to parameters
    /// appended after the SET values (ADR-0037): the row is matched by the
    /// feature's pre-edit identity (<see cref="FeatureId"/>), never by the new
    /// attribute values, so re-keying an identity column cannot retarget the
    /// update onto a different row.
    /// </summary>
    public static string Update(PostgisDatasetName dataset, IFeatureSchema schema, int srid, IReadOnlyList<string> identityColumns)
    {
        var sets = string.Join(", ", schema.Fields.Select((field, i) =>
            field.Kind == AttributeKind.Geometry
                ? $"\"{field.Name}\" = ST_SetSRID(ST_GeomFromEWKB(@p{i}), {srid})"
                : $"\"{field.Name}\" = @p{i}"));
        var predicate = string.Join(" AND ", identityColumns.Select((column, i) => $"\"{column}\" = @p{schema.Count + i}"));
        return $"UPDATE {dataset.QuoteQualified()} SET {sets} WHERE {predicate}";
    }

    /// <summary>Deletes one feature by identity; one bound parameter per identity column, in order (ADR-0037).</summary>
    public static string Delete(PostgisDatasetName dataset, IReadOnlyList<string> identityColumns)
    {
        var predicate = string.Join(" AND ", identityColumns.Select((column, i) => $"\"{column}\" = @p{i}"));
        return $"DELETE FROM {dataset.QuoteQualified()} WHERE {predicate}";
    }

    /// <summary>
    /// Creates the feature-attachment sidecar table (T-088, ADR-0065 §2):
    /// one row per attachment with <c>bytea</c> content, keyed by dataset,
    /// feature identity and the per-feature attachment id. The table is a
    /// fixed provider-owned identifier, so every value the caller supplies
    /// stays a bound parameter.
    /// </summary>
    public static string EnsureAttachmentTable() =>
        "CREATE TABLE IF NOT EXISTS \"public\".\"spatial_attachments\" (\"dataset\" text NOT NULL, \"feature_id\" text NOT NULL, "
        + "\"attachment_id\" bigint NOT NULL, \"name\" text NOT NULL, \"content_type\" text NOT NULL, "
        + "\"size_bytes\" bigint NOT NULL, \"keywords\" text NULL, \"content\" bytea NOT NULL, "
        + "PRIMARY KEY (\"dataset\", \"feature_id\", \"attachment_id\"))";

    /// <summary>Reads the per-feature attachment high-water mark (the next id is one past it, starting at one).</summary>
    public static string MaxAttachmentId() =>
        "SELECT COALESCE(MAX(\"attachment_id\"), 0) FROM \"public\".\"spatial_attachments\" "
        + "WHERE \"dataset\" = @p0 AND \"feature_id\" = @p1";

    /// <summary>Inserts one attachment row; every column — including the <c>bytea</c> content — is a bound parameter.</summary>
    public static string InsertAttachment() =>
        "INSERT INTO \"public\".\"spatial_attachments\" (\"dataset\", \"feature_id\", \"attachment_id\", \"name\", "
        + "\"content_type\", \"size_bytes\", \"keywords\", \"content\") VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6, @p7)";

    /// <summary>Lists one feature's attachment descriptors in attachment-id order.</summary>
    public static string ListAttachments() =>
        "SELECT \"attachment_id\", \"name\", \"content_type\", \"size_bytes\", \"keywords\" "
        + "FROM \"public\".\"spatial_attachments\" WHERE \"dataset\" = @p0 AND \"feature_id\" = @p1 ORDER BY \"attachment_id\"";

    /// <summary>Reads one attachment with its <c>bytea</c> content.</summary>
    public static string GetAttachment() =>
        "SELECT \"attachment_id\", \"name\", \"content_type\", \"size_bytes\", \"keywords\", \"content\" "
        + "FROM \"public\".\"spatial_attachments\" WHERE \"dataset\" = @p0 AND \"feature_id\" = @p1 AND \"attachment_id\" = @p2";

    /// <summary>Replaces one attachment's metadata and <c>bytea</c> content, keeping its identity.</summary>
    public static string UpdateAttachment() =>
        "UPDATE \"public\".\"spatial_attachments\" SET \"name\" = @p3, \"content_type\" = @p4, \"size_bytes\" = @p5, "
        + "\"keywords\" = @p6, \"content\" = @p7 WHERE \"dataset\" = @p0 AND \"feature_id\" = @p1 AND \"attachment_id\" = @p2";

    /// <summary>Deletes one attachment by its per-feature identity.</summary>
    public static string DeleteAttachment() =>
        "DELETE FROM \"public\".\"spatial_attachments\" WHERE \"dataset\" = @p0 AND \"feature_id\" = @p1 AND \"attachment_id\" = @p2";

    /// <summary>
    /// Probes whether a feature exists by its identity tuple (T-088): one
    /// bound parameter per identity column, in order, with <c>LIMIT 1</c>.
    /// Both the table and the identity columns are discovered identifiers,
    /// never client text.
    /// </summary>
    public static string FeatureExists(PostgisDatasetName dataset, IReadOnlyList<string> identityColumns)
    {
        var predicate = string.Join(" AND ", identityColumns.Select((column, i) => $"\"{column}\" = @p{i}"));
        return $"SELECT 1 FROM {dataset.QuoteQualified()} WHERE {predicate} LIMIT 1";
    }

    /// <summary>Creates the result table from a defining batch's schema and per-field geometry typmods.</summary>
    public static string CreateTable(PostgisDatasetName dataset, IFeatureSchema schema, int srid, IReadOnlyList<string> geometryTypes)
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
                builder.Append('"').Append(field.Name).Append("\" geometry(").Append(geometryTypes[i]).Append(", ").Append(srid).Append(')');
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
