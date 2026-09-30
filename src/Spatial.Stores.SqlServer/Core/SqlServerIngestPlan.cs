using System.Text;
using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;

namespace Spatial.Stores.SqlServer.Core;

/// <summary>
/// The pure planning of one SQL Server ingest (ADR-0041 §3): validates the
/// request against the decoded pages and derives the table definition, the
/// identity column and the insert schema. Kept free of SqlClient so the
/// identity rules are unit-testable without a database. Every field name that
/// reaches generated T-SQL is a discovered value validated by
/// <see cref="SqlServerFieldName"/>; it keeps its case and punctuation and is
/// always emitted as a bracketed identifier.
/// </summary>
internal sealed record SqlServerIngestPlan(
    SqlServerDatasetName Dataset,
    int Srid,
    IngestIdentity Identity,
    string IdentityColumn,
    FeatureSchema Schema,
    IReadOnlyList<FeatureBatch> Pages)
{
    /// <summary>The column name an <see cref="IngestIdentity.Auto"/> table assigns.</summary>
    public const string AutoIdentityColumn = "id";

    /// <summary>The total features across every page.</summary>
    public long FeatureCount => Pages.Sum(page => (long)page.Count);

    /// <summary>
    /// The columns the create statement already carries a primary key on —
    /// the identity column, for every ingest (ADR-0149). A column that already
    /// has one is not indexed a second time (ADR-0092).
    /// </summary>
    public IReadOnlyList<string> KeyColumns => [IdentityColumn];

    /// <summary>
    /// The index statements this dataset is created with (ADR-0092): the spatial
    /// index on its primary geometry column and a btree on every attribute
    /// column a pushed-down filter may name. The indexes exist from the first
    /// row, so the loaded dataset is queryable the moment the load commits.
    /// </summary>
    /// <remarks>
    /// The plan's <c>clusteredPrimaryKey</c> is <see cref="KeyColumns"/> being
    /// non-empty: a <c>PRIMARY KEY</c> is clustered unless it says otherwise,
    /// and SQL Server will not grid a table that has none.
    /// </remarks>
    public IReadOnlyList<string> CreateIndexSql() =>
        SqlServerIndexPlan.CreateIndexes(Dataset, Schema, KeyColumns, clusteredPrimaryKey: KeyColumns.Count > 0);

    /// <summary>Validates a request and its pages, or throws <c>invalid.arguments</c>.</summary>
    public static SqlServerIngestPlan Create(IngestRequest request, IReadOnlyList<FeatureBatch> pages)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(pages);
        var dataset = ParseDataset(request);
        ValidateRequest(request, pages);
        var schema = pages[0].Schema;
        SqlServerFieldName.RequireValid(dataset, schema);
        CheckPageSchemas(pages, schema);
        SqlServerGeometryLayout.RequireStorable(schema, pages.SelectMany(page => page.Features));
        var identityColumn = ResolveIdentity(request, schema, dataset);
        return new SqlServerIngestPlan(dataset, request.Srid, request.Identity, identityColumn, schema, pages);
    }

    /// <summary>
    /// The <c>CREATE TABLE</c> statement for this plan, identity/PK included.
    /// SQL Server spatial columns take no typmod, so the SRID is recorded in
    /// the provider's dataset metadata instead of the definition.
    /// </summary>
    public string CreateTableSql()
    {
        var builder = new StringBuilder($"CREATE TABLE {Dataset.QuoteQualified()} (");
        for (var i = 0; i < Schema.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(", ");
            }

            var field = Schema[i];
            builder.Append(SqlServerIdentifier.Quote(field.Name))
                .Append(' ')
                .Append(SqlServerTypeMapping.SqlType(field.Kind));
        }

        if (Identity == IngestIdentity.Auto)
        {
            builder.Append(", ").Append(SqlServerIdentifier.Quote(IdentityColumn)).Append(" bigint IDENTITY(1,1) PRIMARY KEY");
        }
        else
        {
            builder.Append(", PRIMARY KEY (").Append(SqlServerIdentifier.Quote(IdentityColumn)).Append(')');
        }

        return builder.Append(')').ToString();
    }

    /// <summary>
    /// Validates a request against a schema alone, for a streaming load whose
    /// pages have not arrived yet (ADR-0041 §4). The geometry layout check
    /// still needs features, so <see cref="Bind"/> applies it to the first page
    /// before the table is created.
    /// </summary>
    public static SqlServerIngestPlan Create(IngestRequest request, FeatureSchema schema)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(schema);
        var dataset = ParseDataset(request);
        ValidateSrid(request);
        SqlServerFieldName.RequireValid(dataset, schema);
        var identityColumn = ResolveIdentity(request, schema, dataset);
        return new SqlServerIngestPlan(dataset, request.Srid, request.Identity, identityColumn, schema, []);
    }

    /// <summary>The plan with its first page bound and validated against it.</summary>
    public SqlServerIngestPlan Bind(FeatureBatch firstPage)
    {
        ArgumentNullException.ThrowIfNull(firstPage);
        if (firstPage.Count == 0)
        {
            throw SpatialException.BadArguments("An ingest requires at least one feature.");
        }

        if (firstPage.Schema.Equals(Schema) is false)
        {
            throw SpatialException.BadArguments("A page does not share the declared schema.");
        }

        SqlServerGeometryLayout.RequireStorable(Schema, firstPage.Features);
        return this;
    }

    /// <summary>Whether a page may be loaded under this plan.</summary>
    public void CheckPage(FeatureBatch page, int position)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (page.Schema.Equals(Schema) is false)
        {
            throw SpatialException.BadArguments($"Page {position} does not share the declared schema.");
        }
    }

    private static void ValidateSrid(IngestRequest request)
    {
        if (request.Srid <= 0)
        {
            throw SpatialException.BadArguments($"The SRID must be positive, got {request.Srid}.");
        }
    }

    private static SqlServerDatasetName ParseDataset(IngestRequest request)
    {
        if (!SqlServerDatasetName.TryParse(request.Dataset, out var dataset, out var reason))
        {
            throw SpatialException.BadArguments($"Invalid dataset identifier: {reason}");
        }

        return dataset;
    }

    private static void ValidateRequest(IngestRequest request, IReadOnlyList<FeatureBatch> pages)
    {
        ValidateSrid(request);

        if (pages.Count == 0 || pages.Sum(page => page.Count) == 0)
        {
            throw SpatialException.BadArguments("An ingest requires at least one feature.");
        }
    }

    private static void CheckPageSchemas(IReadOnlyList<FeatureBatch> pages, FeatureSchema schema)
    {
        for (var i = 0; i < pages.Count; i++)
        {
            if (!pages[i].Schema.Equals(schema))
            {
                throw SpatialException.BadArguments($"Page {i} does not share the first page's schema.");
            }
        }
    }

    private static string ResolveIdentity(IngestRequest request, FeatureSchema schema, SqlServerDatasetName dataset) =>
        request.Identity switch
        {
            IngestIdentity.Auto => AutoIdentity(request, schema, dataset),
            IngestIdentity.Source => SourceIdentity(request, schema),
            _ => throw SpatialException.BadArguments($"Unsupported ingest identity '{request.Identity}'."),
        };

    private static string AutoIdentity(IngestRequest request, FeatureSchema schema, SqlServerDatasetName dataset)
    {
        RejectIdentityField(request);
        if (schema.IndexOf(AutoIdentityColumn) >= 0)
        {
            throw SpatialException.BadArguments(
                $"The dataset '{dataset}' already has a field named '{AutoIdentityColumn}'; use Identity=Source with an explicit IdentityField.");
        }

        return AutoIdentityColumn;
    }

    private static string SourceIdentity(IngestRequest request, FeatureSchema schema)
    {
        if (string.IsNullOrWhiteSpace(request.IdentityField))
        {
            throw SpatialException.BadArguments("Identity=Source requires an IdentityField name.");
        }

        var index = schema.IndexOf(request.IdentityField);
        if (index < 0)
        {
            throw SpatialException.BadArguments(
                $"The identity field '{request.IdentityField}' is not a field of the ingest schema.");
        }

        if (schema[index].Kind != AttributeKind.Int64)
        {
            throw SpatialException.BadArguments(
                $"The identity field '{request.IdentityField}' must be Int64 but the ingest schema declares {schema[index].Kind}.");
        }

        return request.IdentityField;
    }

    private static void RejectIdentityField(IngestRequest request)
    {
        if (request.IdentityField is not null)
        {
            throw SpatialException.BadArguments("IdentityField is only used when Identity is Source.");
        }
    }
}
