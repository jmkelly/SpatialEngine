using System.Text;
using Spatial.Core.Features;
using Spatial.Stores.SqlServer.Core;

namespace Spatial.Stores.SqlServer.Data;

/// <summary>
/// Every T-SQL statement the store runs (ADR-0028 as the SQL Server provider
/// follows it). Statements are built from validated dataset identifiers and
/// *discovered* column names only — never from client text — so the only free
/// text that can reach SQL is inside bound parameters. The geometry column of
/// a bounding box is bracketed from the dataset description (a discovered
/// identifier); the SRID is an integer from the dataset's own metadata.
/// Deterministic: equal inputs produce equal text.
/// </summary>
internal static class SqlServerQueries
{
    /// <summary>The SRID assumed for a dataset that declares none and holds no geometry.</summary>
    public const int DefaultSrid = 4326;

    /// <summary>The geometry type name reported for a dataset that holds no geometry yet.</summary>
    public const string DefaultGeometryType = "Geometry";

    /// <summary>Full scan: every discovered field, in column order (geometry fields read as WKB bytes).</summary>
    public static string Select(SqlServerDatasetName dataset, IFeatureSchema schema) =>
        $"SELECT {SelectColumns(schema)} FROM {dataset.QuoteQualified()}";

    /// <summary>Query: the scan plus an optional <c>WHERE</c> predicate.</summary>
    public static string Query(SqlServerDatasetName dataset, IFeatureSchema schema, string? predicate) =>
        predicate is null
            ? Select(dataset, schema)
            : $"SELECT {SelectColumns(schema)} FROM {dataset.QuoteQualified()} WHERE {predicate}";

    /// <summary>
    /// Reads features by identity (ADR-0038): one OR-group per requested
    /// identity tuple, one bound parameter per identity value, in input order.
    /// A single tuple carries <c>TOP 1</c> (the primary-key predicate is
    /// already unique); a batch has no limit because each tuple matches at
    /// most one row. Both the identity columns and the dataset identifier are
    /// discovered identifiers, never client text, and the rest of a query plan
    /// (ADR-0074) is <c>AND</c>ed onto the same statement.
    /// </summary>
    public static string SelectByIdentity(
        SqlServerDatasetName dataset,
        IFeatureSchema schema,
        IReadOnlyList<string> identityColumns,
        int count,
        string? predicate = null)
    {
        var limit = count == 1 ? "TOP 1 " : string.Empty;
        var builder = new StringBuilder($"SELECT {limit}{SelectColumns(schema)} FROM {dataset.QuoteQualified()} WHERE ");
        for (var feature = 0; feature < count; feature++)
        {
            if (feature > 0)
            {
                builder.Append(" OR ");
            }

            builder.Append('(')
                .Append(IdentityTuple(identityColumns, feature * identityColumns.Count))
                .Append(')');
        }

        return predicate is null ? builder.ToString() : builder.Append(" AND (").Append(predicate).Append(')').ToString();
    }

    /// <summary>Insert for one feature of a writing batch (one bound parameter per field; geometry as WKB).</summary>
    public static string Insert(SqlServerDatasetName dataset, IFeatureSchema batchSchema, int srid) =>
        InsertCore(dataset, batchSchema, srid, []);

    /// <summary>Insert that returns the store-assigned identity columns (ADR-0037); unchanged when the table has no identity.</summary>
    public static string InsertReturning(
        SqlServerDatasetName dataset, IFeatureSchema batchSchema, int srid, IReadOnlyList<string> identityColumns) =>
        InsertCore(dataset, batchSchema, srid, identityColumns);

    /// <summary>
    /// The insert statement: the column list, the <c>OUTPUT</c> clause (T-SQL
    /// requires it between the columns and the values) and the bound values.
    /// </summary>
    private static string InsertCore(
        SqlServerDatasetName dataset,
        IFeatureSchema batchSchema,
        int srid,
        IReadOnlyList<string> outputColumns)
    {
        var columns = string.Join(", ", batchSchema.Fields.Select(field => SqlServerIdentifier.Quote(field.Name)));
        var values = string.Join(", ", batchSchema.Fields.Select((field, i) => Value(field, i, srid)));
        var output = outputColumns.Count == 0 ? string.Empty : $" OUTPUT {Inserted(outputColumns)}";
        return $"INSERT INTO {dataset.QuoteQualified()} ({columns}){output} VALUES ({values})";
    }

    /// <summary>
    /// Insert that omits the identity columns the batch carried so the
    /// database assigns them (ADR-0043), returning the dataset's identity
    /// columns. Used when the client did not supply an <c>OBJECTID</c>.
    /// </summary>
    public static string InsertWithoutIdentity(
        SqlServerDatasetName dataset,
        IFeatureSchema schema,
        int srid,
        IReadOnlyList<string> omitColumns,
        IReadOnlyList<string> identityColumns)
    {
        var columns = schema.Fields
            .Where(field => !omitColumns.Contains(field.Name, StringComparer.Ordinal))
            .ToArray();
        var names = string.Join(", ", columns.Select(field => SqlServerIdentifier.Quote(field.Name)));
        var values = string.Join(", ", columns.Select((field, i) => Value(field, i, srid)));
        return $"INSERT INTO {dataset.QuoteQualified()} ({names}) OUTPUT {Inserted(identityColumns)} VALUES ({values})";
    }

    /// <summary>
    /// Updates one feature in place. The identity predicate binds to parameters
    /// appended after the SET values (ADR-0037): the row is matched by the
    /// feature's pre-edit identity (<see cref="FeatureId"/>), never by the new
    /// attribute values, so re-keying an identity column cannot retarget the
    /// update onto a different row.
    /// </summary>
    public static string Update(
        SqlServerDatasetName dataset, IFeatureSchema schema, int srid, IReadOnlyList<string> identityColumns)
    {
        var sets = string.Join(", ", schema.Fields.Select((field, i) =>
            $"{SqlServerIdentifier.Quote(field.Name)} = {Value(field, i, srid)}"));
        return $"UPDATE {dataset.QuoteQualified()} SET {sets} WHERE {IdentityTuple(identityColumns, schema.Count)}";
    }

    /// <summary>Deletes one feature by identity; one bound parameter per identity column, in order (ADR-0037).</summary>
    public static string Delete(SqlServerDatasetName dataset, IReadOnlyList<string> identityColumns) =>
        $"DELETE FROM {dataset.QuoteQualified()} WHERE {IdentityTuple(identityColumns, 0)}";

    /// <summary>
    /// Probes whether a feature exists by its identity tuple (T-088): one
    /// bound parameter per identity column, in order, with <c>TOP 1</c>. Both
    /// the table and the identity columns are discovered identifiers, never
    /// client text.
    /// </summary>
    public static string FeatureExists(SqlServerDatasetName dataset, IReadOnlyList<string> identityColumns) =>
        $"SELECT TOP 1 1 FROM {dataset.QuoteQualified()} WHERE {IdentityTuple(identityColumns, 0)}";

    /// <summary>
    /// Creates the result table from a defining batch's schema. SQL Server
    /// spatial columns carry no typmod, so the SRID is recorded in the
    /// provider's dataset metadata rather than in the definition.
    /// </summary>
    public static string CreateTable(SqlServerDatasetName dataset, IFeatureSchema schema)
    {
        var builder = new StringBuilder($"CREATE TABLE {dataset.QuoteQualified()} (");
        for (var i = 0; i < schema.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(", ");
            }

            var field = schema[i];
            builder.Append(SqlServerIdentifier.Quote(field.Name))
                .Append(' ')
                .Append(SqlServerTypeMapping.SqlType(field.Kind));
        }

        return builder.Append(')').ToString();
    }

    /// <summary>
    /// The spatial index of a dataset's primary geometry column (ADR-0092):
    /// SQL Server grids a <c>geometry</c> column with
    /// <c>GEOMETRY_AUTO_GRID</c>, which is what the bounding-box
    /// <c>STIntersects</c> pushdown predicate can seek. The
    /// <c>BOUNDING_BOX</c> is a required clause rather than a limit: with
    /// <c>AUTO_GRID</c> the server recomputes the grid from the data, so a
    /// geometry stored outside the declared box is still found (verified
    /// against the container the tests run). The dataset identifier and the
    /// column are validated identifiers, never client text.
    /// </summary>
    public static string CreateSpatialIndex(SqlServerDatasetName dataset, string column, string gridType) =>
        $"CREATE SPATIAL INDEX {SqlServerIdentifier.Quote(SqlServerIndexName.For(dataset.Table, column))} "
        + $"ON {dataset.QuoteQualified()} ({SqlServerIdentifier.Quote(column)}) USING {gridType} "
        + "WITH (BOUNDING_BOX = (-180, -90, 180, 90), CELLS_PER_OBJECT = 16)";

    /// <summary>
    /// The btree index of one attribute column (ADR-0092), which is what the
    /// equality and range comparisons of a pushed-down attribute filter can
    /// seek. The dataset identifier and the column are validated identifiers.
    /// </summary>
    public static string CreateBtreeIndex(SqlServerDatasetName dataset, string column) =>
        $"CREATE INDEX {SqlServerIdentifier.Quote(SqlServerIndexName.For(dataset.Table, column))} "
        + $"ON {dataset.QuoteQualified()} ({SqlServerIdentifier.Quote(column)})";

    /// <summary>
    /// The provider-owned dataset metadata sidecar (ADR-0028 §3): the SRID a
    /// table was created or ingested with, keyed by its qualified name. SQL
    /// Server has no per-column SRID to discover, so an empty table's CRS is
    /// read back from here instead of being guessed.
    /// </summary>
    public static string EnsureDatasetMetadataTable() =>
        "IF OBJECT_ID(N'spatial_datasets', N'U') IS NULL CREATE TABLE spatial_datasets "
        + "(dataset nvarchar(300) NOT NULL PRIMARY KEY, srid int NOT NULL)";

    /// <summary>Records (or replaces) the SRID a dataset was created with.</summary>
    public static string UpsertDatasetSrid() =>
        "UPDATE spatial_datasets SET srid = @p1 WHERE dataset = @p0; "
        + "IF @@ROWCOUNT = 0 INSERT INTO spatial_datasets (dataset, srid) VALUES (@p0, @p1);";

    /// <summary>Reads the recorded SRID of one dataset, or no row when the table was not made here.</summary>
    public static string DatasetSrid() =>
        "IF OBJECT_ID(N'spatial_datasets', N'U') IS NOT NULL SELECT TOP 1 srid FROM spatial_datasets WHERE dataset = @p0";

    /// <summary>The planner's row-count estimate for one table, from the partition statistics.</summary>
    public static string RowEstimate() =>
        "SELECT ISNULL(SUM(ps.row_count), 0) "
        + "FROM sys.dm_db_partition_stats ps "
        + "JOIN sys.tables t ON t.object_id = ps.object_id "
        + "JOIN sys.schemas s ON s.schema_id = t.schema_id "
        + "WHERE s.name = @p0 AND t.name = @p1 AND ps.index_id IN (0, 1)";

    /// <summary>
    /// The SRID and geometry type of one dataset's first non-null value. SQL
    /// Server stores the SRID with the value rather than on the column, so an
    /// authored table is discovered by sampling its data; an empty table falls
    /// back to the dataset's recorded SRID and then to
    /// <see cref="DefaultSrid"/>.
    /// </summary>
    public static string SampleGeometry(SqlServerDatasetName dataset, string geometryColumn) =>
        $"SELECT TOP 1 {Quoted(geometryColumn)}.STSrid, {Quoted(geometryColumn)}.STGeometryType() "
        + $"FROM {dataset.QuoteQualified()} WHERE {Quoted(geometryColumn)} IS NOT NULL";
    /// <summary>
    /// One statement sampling the SRID and geometry type of every listed
    /// dataset, so the whole catalogue costs one round trip. Each arm carries
    /// its catalogue ordinal so the union is ordered deterministically; a
    /// dataset with no rows contributes NULL samples. The names come from the
    /// catalogue query itself (discovered, then bracket-quoted), never from
    /// client text.
    /// </summary>
    public static string SampleAllGeometry(IReadOnlyList<GeometrySample> datasets)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < datasets.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(" UNION ALL ");
            }

            builder.Append(SampleArm(datasets[i], i));
        }

        return builder.Append(" ORDER BY ordinal").ToString();
    }

    private static string SampleArm(GeometrySample sample, int ordinal)
    {
        // The spatial property must be reached through the column of the
        // aliased table (`g.[geom].STSrid`); a bare table alias (`g.STSrid`)
        // is not a resolvable property reference in T-SQL.
        var source = $"{sample.Dataset.QuoteQualified()} AS g WHERE g.{Quoted(sample.GeometryColumn)} IS NOT NULL";
        var property = $"g.{Quoted(sample.GeometryColumn)}";
        return $"SELECT {ordinal} AS ordinal, (SELECT TOP 1 {property}.STSrid FROM {source}) AS srid, "
            + $"(SELECT TOP 1 {property}.STGeometryType() FROM {source}) AS geometry_type";
    }

    /// <summary>Lists every spatial table (schema, table, its first spatial column, row estimate).</summary>
    public static string Catalogue(string? pattern)
    {
        var filter = pattern is null ? string.Empty : " AND (s.name + '.' + t.name) LIKE @p0";
        return "SELECT s.name, t.name, g.name, ISNULL(st.row_count, 0) "
            + "FROM sys.tables t "
            + "JOIN sys.schemas s ON s.schema_id = t.schema_id "
            + "OUTER APPLY (SELECT TOP 1 c.name AS name FROM sys.columns c "
            + "              WHERE c.object_id = t.object_id AND TYPE_NAME(c.user_type_id) IN ('geometry', 'geography') "
            + "              ORDER BY c.column_id) g "
            + "OUTER APPLY (SELECT SUM(row_count) AS row_count FROM sys.dm_db_partition_stats ps "
            + "              WHERE ps.object_id = t.object_id AND ps.index_id IN (0, 1)) st "
            + "WHERE g.name IS NOT NULL"
            + filter
            + " ORDER BY s.name, t.name";
    }

    /// <summary>Column metadata for one table (name, type, nullability, ordinal).</summary>
    public static string ColumnsMetadata() =>
        "SELECT c.name, TYPE_NAME(c.user_type_id), c.is_nullable, c.column_id "
        + "FROM sys.columns c "
        + "JOIN sys.tables t ON t.object_id = c.object_id "
        + "JOIN sys.schemas s ON s.schema_id = t.schema_id "
        + "WHERE s.name = @p0 AND t.name = @p1 "
        + "ORDER BY c.column_id";

    /// <summary>Primary-key column names for one table, in key order.</summary>
    public static string PrimaryKeyColumns() =>
        "SELECT c.name "
        + "FROM sys.indexes i "
        + "JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id "
        + "JOIN sys.columns c ON c.object_id = i.object_id AND c.column_id = ic.column_id "
        + "JOIN sys.tables t ON t.object_id = i.object_id "
        + "JOIN sys.schemas s ON s.schema_id = t.schema_id "
        + "WHERE i.is_primary_key = 1 AND s.name = @p0 AND t.name = @p1 "
        + "ORDER BY ic.key_ordinal";

    /// <summary>
    /// Creates the feature-attachment sidecar table (T-088, ADR-0065 §2): one
    /// row per attachment with <c>varbinary(max)</c> content, keyed by dataset,
    /// feature identity and the per-feature attachment id. The table is a fixed
    /// provider-owned identifier, so every value the caller supplies stays a
    /// bound parameter.
    /// </summary>
    public static string EnsureAttachmentTable() =>
        "IF OBJECT_ID(N'spatial_attachments', N'U') IS NULL CREATE TABLE spatial_attachments "
        + "(dataset nvarchar(300) NOT NULL, feature_id nvarchar(300) NOT NULL, attachment_id bigint NOT NULL, "
        + "name nvarchar(200) NOT NULL, content_type nvarchar(100) NOT NULL, size_bytes bigint NOT NULL, "
        + "keywords nvarchar(400) NULL, content varbinary(max) NOT NULL, "
        + "PRIMARY KEY (dataset, feature_id, attachment_id))";

    /// <summary>Reads the per-feature attachment high-water mark (the next id is one past it, starting at one).</summary>
    public static string MaxAttachmentId() =>
        "SELECT ISNULL(MAX(attachment_id), 0) FROM spatial_attachments "
        + "WHERE dataset = @p0 AND feature_id = @p1";

    /// <summary>Inserts one attachment row; every column — including the content — is a bound parameter.</summary>
    public static string InsertAttachment() =>
        "INSERT INTO spatial_attachments (dataset, feature_id, attachment_id, name, content_type, size_bytes, keywords, content) "
        + "VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6, @p7)";

    /// <summary>Lists one feature's attachment descriptors in attachment-id order.</summary>
    public static string ListAttachments() =>
        "SELECT attachment_id, name, content_type, size_bytes, keywords "
        + "FROM spatial_attachments WHERE dataset = @p0 AND feature_id = @p1 ORDER BY attachment_id";

    /// <summary>Reads one attachment with its content.</summary>
    public static string GetAttachment() =>
        "SELECT attachment_id, name, content_type, size_bytes, keywords, content "
        + "FROM spatial_attachments WHERE dataset = @p0 AND feature_id = @p1 AND attachment_id = @p2";

    /// <summary>Replaces one attachment's metadata and content, keeping its identity.</summary>
    public static string UpdateAttachment() =>
        "UPDATE spatial_attachments SET name = @p3, content_type = @p4, size_bytes = @p5, keywords = @p6, content = @p7 "
        + "WHERE dataset = @p0 AND feature_id = @p1 AND attachment_id = @p2";

    /// <summary>Deletes one attachment by its per-feature identity.</summary>
    public static string DeleteAttachment() =>
        "DELETE FROM spatial_attachments WHERE dataset = @p0 AND feature_id = @p1 AND attachment_id = @p2";

    /// <summary>Scan/query columns: plain names, geometry fields wrapped so SQL Server returns canonical WKB bytes.</summary>
    private static string SelectColumns(IFeatureSchema schema) =>
        string.Join(", ", schema.Fields.Select(field => field.Kind == AttributeKind.Geometry
            ? $"{Quoted(field.Name)}.STAsBinary()"
            : Quoted(field.Name)));

    /// <summary>The identity columns as one bound predicate, numbering its parameters from <paramref name="from"/>.</summary>
    private static string IdentityTuple(IReadOnlyList<string> identityColumns, int from) =>
        string.Join(" AND ", identityColumns.Select((column, i) => $"{Quoted(column)} = @p{from + i}"));

    private static string Inserted(IReadOnlyList<string> identityColumns) =>
        string.Join(", ", identityColumns.Select(column => $"INSERTED.{Quoted(column)}"));

    /// <summary>
    /// One bound value for a field: geometry is WKB rebuilt at the dataset's
    /// SRID (a discovered integer), everything else the parameter itself.
    /// </summary>
    private static string Value(FieldDefinition field, int index, int srid) => field.Kind == AttributeKind.Geometry
        ? $"geometry::STGeomFromWKB(@p{index}, {srid})"
        : $"@p{index}";

    private static string Quoted(string name) => SqlServerIdentifier.Quote(name);
}

/// <summary>One dataset to sample geometry facts for (<see cref="SqlServerQueries.SampleAllGeometry"/>).</summary>
internal readonly record struct GeometrySample(SqlServerDatasetName Dataset, string GeometryColumn);
