using System.Text;
using Spatial.Core.Features;
using Spatial.Stores.PostGIS.Core;

namespace Spatial.Stores.PostGIS.Data;

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
    /// discovered identifiers, never client text, and the rest of a query
    /// plan (ADR-0074) is <c>AND</c>ed onto the same statement — the identity
    /// values come first, so the plan's own parameters continue after them.
    /// A text identity states the byte order it is compared in, so the lookup
    /// cannot answer with the row a case-folding collation folded it onto
    /// (ADR-0126).
    /// </summary>
    public static string SelectByIdentity(
        PostgisDatasetName dataset,
        FeatureSchema schema,
        IReadOnlyList<string> identityColumns,
        int count,
        bool byteOrderText,
        string? predicate = null)
    {
        var builder = new StringBuilder($"SELECT {SelectColumns(schema)} FROM {dataset.QuoteQualified()} WHERE ");
        for (var feature = 0; feature < count; feature++)
        {
            if (feature > 0)
            {
                builder.Append(" OR ");
            }

            builder.Append('(')
                .Append(PostgisIdentity.Tuple(schema, identityColumns, feature * identityColumns.Count, byteOrderText))
                .Append(')');
        }

        var statement = builder.ToString();
        if (predicate is not null)
        {
            statement += $" AND ({predicate})";
        }

        return count == 1 ? statement + " LIMIT 1" : statement;
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
    /// update onto a different row. The <c>SET</c> list is the <em>batch's</em>
    /// schema and the predicate resolves the identity against the
    /// <em>dataset's</em>, because a batch need not carry the identity column
    /// at all (ADR-0126).
    /// </summary>
    public static string Update(
        PostgisDatasetName dataset,
        FeatureSchema batchSchema,
        int srid,
        IReadOnlyList<string> identityColumns,
        FeatureSchema identitySchema,
        bool byteOrderText)
    {
        var sets = string.Join(", ", batchSchema.Fields.Select((field, i) =>
            field.Kind == AttributeKind.Geometry
                ? $"\"{field.Name}\" = ST_SetSRID(ST_GeomFromEWKB(@p{i}), {srid})"
                : $"\"{field.Name}\" = @p{i}"));
        return $"UPDATE {dataset.QuoteQualified()} SET {sets} "
            + $"WHERE {PostgisIdentity.Tuple(identitySchema, identityColumns, batchSchema.Count, byteOrderText)}";
    }

    /// <summary>Deletes one feature by identity; one bound parameter per identity column, in order (ADR-0037, ADR-0126).</summary>
    public static string Delete(
        PostgisDatasetName dataset, FeatureSchema schema, IReadOnlyList<string> identityColumns, bool byteOrderText) =>
        $"DELETE FROM {dataset.QuoteQualified()} WHERE {PostgisIdentity.Tuple(schema, identityColumns, 0, byteOrderText)}";

    /// <summary>
    /// Creates the feature-attachment sidecar table (T-088, ADR-0065 §2):
    /// one row per attachment with <c>bytea</c> content, keyed by dataset,
    /// feature identity and the per-feature attachment id. The table is a
    /// fixed provider-owned identifier, so every value the caller supplies
    /// stays a bound parameter. Its two identity columns declare the byte
    /// order the contract compares them in, because this is the store's own
    /// table and this is the one place that declaration has a consumer
    /// (ADR-0130). The declaration is unconditional: it costs nothing and
    /// needs no catalog read, unlike the term a statement over an
    /// <em>authored</em> table must decide from the database (ADR-0123).
    /// </summary>
    public static string EnsureAttachmentTable() =>
        "CREATE TABLE IF NOT EXISTS \"public\".\"spatial_attachments\" (\"dataset\" text COLLATE \"C\" NOT NULL, "
        + "\"feature_id\" text COLLATE \"C\" NOT NULL, \"attachment_id\" bigint NOT NULL, \"name\" text NOT NULL, "
        + "\"content_type\" text NOT NULL, \"size_bytes\" bigint NOT NULL, \"keywords\" text NULL, "
        + "\"content\" bytea NOT NULL, PRIMARY KEY (\"dataset\", \"feature_id\", \"attachment_id\"))";

    /// <summary>
    /// Brings a sidecar created by an earlier version forward (ADR-0130):
    /// <c>CREATE TABLE IF NOT EXISTS</c> will not re-declare a table that
    /// already exists, so a sidecar whose identity columns carry a
    /// case-folding collation is re-declared here under <c>COLLATE "C"</c>.
    /// The step is guarded by each column's own recorded collation, so it is
    /// one catalog read and no rewrite once the table carries the declaration.
    /// </summary>
    public static string RecollateAttachmentIdentity() =>
        "DO $sidecar$ BEGIN "
        + "IF EXISTS (SELECT 1 FROM pg_attribute a JOIN pg_collation c ON c.oid = a.attcollation "
        + "WHERE a.attrelid = to_regclass('public.spatial_attachments') AND a.attname = 'dataset' AND c.collname <> 'C') THEN "
        + "ALTER TABLE \"public\".\"spatial_attachments\" ALTER COLUMN \"dataset\" TYPE text COLLATE \"C\"; END IF; "
        + "IF EXISTS (SELECT 1 FROM pg_attribute a JOIN pg_collation c ON c.oid = a.attcollation "
        + "WHERE a.attrelid = to_regclass('public.spatial_attachments') AND a.attname = 'feature_id' AND c.collname <> 'C') THEN "
        + "ALTER TABLE \"public\".\"spatial_attachments\" ALTER COLUMN \"feature_id\" TYPE text COLLATE \"C\"; END IF; "
        + "END $sidecar$";

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
    public static string FeatureExists(
        PostgisDatasetName dataset, FeatureSchema schema, IReadOnlyList<string> identityColumns, bool byteOrderText) =>
        $"SELECT 1 FROM {dataset.QuoteQualified()} "
        + $"WHERE {PostgisIdentity.Tuple(schema, identityColumns, 0, byteOrderText)} LIMIT 1";

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

    /// <summary>
    /// The spatial index of a dataset's primary geometry column (ADR-0092):
    /// PostGIS indexes geometry with GiST, which is what the bounding-box
    /// <c>&amp;&amp;</c> pushdown predicate can seek. The dataset identifier and
    /// the column are validated identifiers, never client text.
    /// </summary>
    public static string CreateSpatialIndex(PostgisDatasetName dataset, string column) =>
        $"CREATE INDEX \"{PostgisIndexName.For(dataset.Table, column)}\" ON {dataset.QuoteQualified()} USING GIST (\"{column}\")";

    /// <summary>
    /// The btree index of one attribute column (ADR-0092), which is what the
    /// equality and range comparisons of a pushed-down attribute filter can
    /// seek. The dataset identifier and the column are validated identifiers.
    /// </summary>
    public static string CreateBtreeIndex(PostgisDatasetName dataset, string column) =>
        $"CREATE INDEX \"{PostgisIndexName.For(dataset.Table, column)}\" ON {dataset.QuoteQualified()} (\"{column}\")";

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

    /// <summary>
    /// Each column's declared type modifier (ADR-0083): the formatted
    /// PostgreSQL type, which for a PostGIS geometry column carries the
    /// dimension — <c>geometry(PointZ,4326)</c> declares a Z, a plain
    /// <c>geometry</c> declares nothing. Read from the catalogue, never from
    /// the data, so it costs no scan and cannot disagree with the schema.
    /// </summary>
    public static string ColumnTypeModifiers() =>
        "SELECT a.attname, pg_catalog.format_type(a.atttypid, a.atttypmod) "
        + "FROM pg_catalog.pg_attribute a "
        + "JOIN pg_catalog.pg_class c ON c.oid = a.attrelid "
        + "JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace "
        + "WHERE n.nspname = @p0 AND c.relname = @p1 AND a.attnum > 0 AND NOT a.attisdropped "
        + "ORDER BY a.attnum";

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

    /// <summary>
    /// The collation this database was created with, which is the collation a
    /// <c>text</c> column carries unless it declares one of its own — and so the
    /// comparison an <c>ORDER BY</c> over a text sort key inherits when the
    /// store does not say otherwise (ADR-0121). Read once per store: it is a
    /// property of the database, not of a query, and it never changes while a
    /// connection is open.
    /// </summary>
    public static string DatabaseCollation() =>
        "SELECT datcollate FROM pg_database WHERE datname = current_database()";

    /// <summary>
    /// The dataset's durable content-version table (ADR-0129), created in the
    /// dataset's <em>own</em> schema so it needs no privilege the write itself
    /// did not need, and so dropping the schema drops its versions. Idempotent,
    /// so the write paths may issue it on every write rather than keep a
    /// process-local "already created" flag that a second process would not
    /// share.
    /// </summary>
    public static string CreateVersionTable(PostgisDatasetName dataset) =>
        $"CREATE TABLE IF NOT EXISTS {VersionTable(dataset)} (" +
        "\"dataset\" text PRIMARY KEY, \"version\" bigint NOT NULL)";

    /// <summary>
    /// Moves the version: insert the first row, or increment the one there is.
    /// One statement, so a concurrent writer never reads-then-writes.
    /// </summary>
    public static string BumpVersion(PostgisDatasetName dataset) =>
        $"INSERT INTO {VersionTable(dataset)} (\"dataset\", \"version\") VALUES (@p0, 1) " +
        $"ON CONFLICT (\"dataset\") DO UPDATE SET \"version\" = {PostgisContentVersionTable}.\"version\" + 1";

    /// <summary>Reads the dataset's version; no row means the engine has never written it.</summary>
    public static string SelectVersion(PostgisDatasetName dataset) =>
        $"SELECT \"version\" FROM {VersionTable(dataset)} WHERE \"dataset\" = @p0";

    /// <summary>The version table's name, a constant so the read and the write cannot disagree on it.</summary>
    public const string PostgisContentVersionTable = "spatial_dataset_version";

    private static string VersionTable(PostgisDatasetName dataset) =>
        $"\"{dataset.Schema}\".\"{PostgisContentVersionTable}\"";

    /// <summary>Whether a table exists at all (drives the 'unknown dataset' diagnostic).</summary>
    public static string TableExists() =>
        "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = @p0 AND table_name = @p1)";

    /// <summary>Scan/query columns: plain names, geometry fields wrapped so PostGIS returns canonical EWKB bytes.</summary>
    private static string SelectColumns(IFeatureSchema schema) =>
        string.Join(", ", schema.Fields.Select(field =>
            field.Kind == AttributeKind.Geometry ? $"ST_AsEWKB(\"{field.Name}\")" : $"\"{field.Name}\""));
}
