using System.Text;
using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;

namespace Spatial.Stores.PostGIS.Core;

/// <summary>
/// The pure planning of one PostGIS ingest (ADR-0041 §3): validates the
/// request against the decoded pages and derives the table definition, the
/// identity column and the insert schema. Kept free of Npgsql so the identity
/// rules are unit-testable without a database. Every field name that reaches
/// generated SQL is a discovered value validated by <see cref="PostgisFieldName"/>;
/// it keeps its case and punctuation and is always emitted as a quoted
/// identifier.
/// </summary>
internal sealed record PostgisIngestPlan(
    PostgisDatasetName Dataset,
    int Srid,
    IngestIdentity Identity,
    string? IdentityColumn,
    FeatureSchema Schema,
    IReadOnlyList<FeatureBatch> Pages,
    IReadOnlyList<string> GeometryTypes)
{
    /// <summary>The column name an <see cref="IngestIdentity.Auto"/> table assigns.</summary>
    public const string AutoIdentityColumn = "id";

    /// <summary>Total features across every page.</summary>
    public long FeatureCount => Pages.Sum(page => (long)page.Count);

    /// <summary>Validates a request and its pages, or throws <c>invalid.arguments</c>.</summary>
    public static PostgisIngestPlan Create(IngestRequest request, IReadOnlyList<FeatureBatch> pages)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(pages);
        var dataset = ParseDataset(request);
        ValidateRequest(request, pages);
        var schema = pages[0].Schema;
        CheckIdentifier(dataset, schema);
        CheckPageSchemas(pages, schema);
        var geometryTypes = ResolveGeometryTypes(dataset, schema, pages);
        var identityColumn = ResolveIdentity(request, schema, dataset);
        return new PostgisIngestPlan(dataset, request.Srid, request.Identity, identityColumn, schema, pages, geometryTypes);
    }

    /// <summary>
    /// Validates a request against a schema alone, for a streaming load whose
    /// pages have not arrived yet (ADR-0041 §4). The geometry column types
    /// still need features, so they are resolved by <see cref="Bind"/> once the
    /// first page lands and the table is created from there.
    /// </summary>
    public static PostgisIngestPlan Create(IngestRequest request, FeatureSchema schema)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(schema);
        var dataset = ParseDataset(request);
        ValidateSrid(request);
        CheckIdentifier(dataset, schema);
        var identityColumn = ResolveIdentity(request, schema, dataset);
        return new PostgisIngestPlan(dataset, request.Srid, request.Identity, identityColumn, schema, [], []);
    }

    /// <summary>
    /// The plan with its first page bound, which fixes the geometry column
    /// types the <c>CREATE TABLE</c> statement needs. Every later page must
    /// share the declared schema.
    /// </summary>
    public PostgisIngestPlan Bind(FeatureBatch firstPage)
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

        return this with { GeometryTypes = ResolveGeometryTypes(Dataset, Schema, [firstPage]) };
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

    private static PostgisDatasetName ParseDataset(IngestRequest request)
    {
        if (!PostgisDatasetName.TryParse(request.Dataset, out var dataset, out var reason))
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

    private static string[] ResolveGeometryTypes(
        PostgisDatasetName dataset, FeatureSchema schema, IReadOnlyList<FeatureBatch> pages)
    {
        if (!PostgisGeometryType.TryResolve(schema, pages.SelectMany(page => page.Features), out var typeNames, out var error))
        {
            throw SpatialException.BadArguments($"The dataset '{dataset}' cannot be created: {error}");
        }

        return typeNames;
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

    /// <summary>The <c>CREATE TABLE</c> statement for this plan, identity/PK included.</summary>
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
            builder.Append('"').Append(field.Name).Append('"');
            if (field.Kind == AttributeKind.Geometry)
            {
                builder.Append(" geometry(").Append(GeometryTypes[i]).Append(", ").Append(Srid).Append(')');
            }
            else
            {
                builder.Append(' ').Append(PostgisTypeMapping.SqlType(field.Kind));
            }
        }

        if (Identity == IngestIdentity.Auto)
        {
            builder.Append(", \"").Append(IdentityColumn).Append("\" bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY");
        }
        else if (Identity == IngestIdentity.Source)
        {
            builder.Append(", PRIMARY KEY (\"").Append(IdentityColumn).Append("\")");
        }

        return builder.Append(')').ToString();
    }

    private static string? ResolveIdentity(IngestRequest request, FeatureSchema schema, PostgisDatasetName dataset) =>
        request.Identity switch
        {
            IngestIdentity.None => NoneIdentity(request),
            IngestIdentity.Auto => AutoIdentity(request, schema, dataset),
            IngestIdentity.Source => SourceIdentity(request, schema),
            _ => throw SpatialException.BadArguments($"Unsupported ingest identity '{request.Identity}'."),
        };

    private static string? NoneIdentity(IngestRequest request)
    {
        RejectIdentityField(request);
        return null;
    }

    private static string? AutoIdentity(IngestRequest request, FeatureSchema schema, PostgisDatasetName dataset)
    {
        RejectIdentityField(request);
        if (schema.IndexOf(AutoIdentityColumn) >= 0)
        {
            throw SpatialException.BadArguments(
                $"The dataset '{dataset}' already has a field named '{AutoIdentityColumn}'; use Identity=Source with an explicit IdentityField.");
        }

        return AutoIdentityColumn;
    }

    private static string? SourceIdentity(IngestRequest request, FeatureSchema schema)
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

    /// <summary>Rejects any field name that cannot be carried as a quoted PostgreSQL identifier.</summary>
    private static void CheckIdentifier(PostgisDatasetName dataset, FeatureSchema schema) =>
        PostgisFieldName.RequireValid(dataset, schema);
}
