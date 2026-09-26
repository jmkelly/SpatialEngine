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
    string? IdentityColumn,
    FeatureSchema Schema,
    IReadOnlyList<FeatureBatch> Pages)
{
    /// <summary>The column name an <see cref="IngestIdentity.Auto"/> table assigns.</summary>
    public const string AutoIdentityColumn = "id";

    /// <summary>Total features across every page.</summary>
    public long FeatureCount => Pages.Sum(page => (long)page.Count);

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
            builder.Append(", ").Append(IdentityColumnSql()).Append(" bigint IDENTITY(1,1) PRIMARY KEY");
        }
        else if (Identity == IngestIdentity.Source)
        {
            builder.Append(", PRIMARY KEY (").Append(SqlServerIdentifier.Quote(IdentityColumn!)).Append(')');
        }

        return builder.Append(')').ToString();
    }

    private static string IdentityColumnSql() => SqlServerIdentifier.Quote(AutoIdentityColumn);

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
        if (request.Srid <= 0)
        {
            throw SpatialException.BadArguments($"The SRID must be positive, got {request.Srid}.");
        }

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

    private static string? ResolveIdentity(IngestRequest request, FeatureSchema schema, SqlServerDatasetName dataset) =>
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

    private static string? AutoIdentity(IngestRequest request, FeatureSchema schema, SqlServerDatasetName dataset)
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
}
