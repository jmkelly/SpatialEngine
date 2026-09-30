using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;

namespace Spatial.Stores.Memory;

/// <summary>
/// The validated plan of one in-memory ingest (ADR-0042): the target dataset,
/// identity mode and the stored schema (with the appended auto-identity
/// column), plus the pages to load. Pure and top-level so the identity rules
/// are unit-testable without the catalog (the same factoring as PostGIS's
/// <c>PostgisIngestPlan</c>). Every plan has an identity column (ADR-0149),
/// so a stored feature is keyed by it: an auto ingest's assigned id or a
/// source ingest's own column (ADR-0038, ADR-0119).
/// </summary>
internal sealed record MemoryIngestPlan(
    string Dataset, int Srid, IngestIdentity Identity, string IdentityColumn,
    FeatureSchema SourceSchema, FeatureSchema StoredSchema, IReadOnlyList<FeatureBatch> Pages)
{
    /// <summary>Validates a request and its pages, or throws <c>invalid.arguments</c>.</summary>
    public static MemoryIngestPlan Create(IngestRequest request, IReadOnlyList<FeatureBatch> pages)
    {
        ValidateRequest(request);
        ValidatePages(pages);
        var (identity, identityColumn, storedSchema) = IdentityOf(request, pages[0].Schema);
        return new MemoryIngestPlan(
            request.Dataset, request.Srid, identity, identityColumn, pages[0].Schema, storedSchema, pages);
    }

    /// <summary>
    /// Validates a request against a schema alone, for a streaming load whose
    /// pages have not arrived yet (ADR-0041 §4). The same rules apply; only the
    /// "at least one feature" check waits for the pages.
    /// </summary>
    public static MemoryIngestPlan Create(IngestRequest request, FeatureSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ValidateRequest(request);
        var (identity, identityColumn, storedSchema) = IdentityOf(request, schema);
        return new MemoryIngestPlan(request.Dataset, request.Srid, identity, identityColumn, schema, storedSchema, []);
    }

    /// <summary>
    /// The plan with its pages bound, after checking that they share the
    /// declared schema. The in-memory store keeps every feature, so this is
    /// where the pages are materialised — what the streaming face saves is the
    /// decoded document, not the dataset.
    /// </summary>
    public MemoryIngestPlan Bind(IReadOnlyList<FeatureBatch> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        ValidatePages(pages, SourceSchema);
        return this with { Pages = pages };
    }

    /// <summary>Builds the dataset and its stored features in one step (atomic by construction).</summary>
    public MemoryDataset Materialise()
    {
        var features = new List<Feature>(Pages.Sum(page => (int)page.Count));
        long nextId = 1;
        var template = MemoryDataset.Create(Dataset, Srid, StoredSchema, idColumns: [], features: [], nextId: 1);
        var identityIndex = StoredSchema.IndexOf(IdentityColumn);
        foreach (var page in Pages)
        {
            foreach (var feature in page.Features)
            {
                long? assigned = Identity == IngestIdentity.Auto ? nextId++ : null;
                var stored = MemorySchema.BuildStored(template, feature, assigned);
                features.Add(Identity == IngestIdentity.Source ? MemorySchema.Rekey(stored, identityIndex) : stored);
            }
        }

        return MemoryDataset.Create(Dataset, Srid, StoredSchema, [IdentityColumn], features, nextId);
    }

    private static void ValidateRequest(IngestRequest request)
    {
        if (!MemoryDataset.IsValidId(request.Dataset))
        {
            throw SpatialException.BadArguments($"Invalid dataset identifier '{request.Dataset}': expected schema.table.");
        }

        if (request.Srid <= 0)
        {
            throw SpatialException.BadArguments($"The SRID must be positive, got {request.Srid}.");
        }
    }

    private static void ValidatePages(IReadOnlyList<FeatureBatch> pages)
    {
        if (pages.Count == 0 || pages.Sum(page => page.Count) == 0)
        {
            throw SpatialException.BadArguments("An ingest requires at least one feature.");
        }

        for (var i = 0; i < pages.Count; i++)
        {
            if (!pages[i].Schema.Equals(pages[0].Schema))
            {
                throw SpatialException.BadArguments($"Page {i} does not share the first page's schema.");
            }
        }
    }

    /// <summary>The page checks against a declared schema rather than page zero.</summary>
    private static void ValidatePages(IReadOnlyList<FeatureBatch> pages, FeatureSchema schema)
    {
        if (pages.Count == 0 || pages.Sum(page => page.Count) == 0)
        {
            throw SpatialException.BadArguments("An ingest requires at least one feature.");
        }

        for (var i = 0; i < pages.Count; i++)
        {
            if (!pages[i].Schema.Equals(schema))
            {
                throw SpatialException.BadArguments($"Page {i} does not share the declared schema.");
            }
        }
    }

    private static (IngestIdentity Identity, string Column, FeatureSchema Schema) IdentityOf(
        IngestRequest request, FeatureSchema source) => request.Identity switch
        {
            IngestIdentity.Auto => Auto(request, source),
            IngestIdentity.Source => Source(request, source),
            _ => throw SpatialException.BadArguments($"Unsupported ingest identity '{request.Identity}'."),
        };

    private static (IngestIdentity, string, FeatureSchema) Auto(IngestRequest request, FeatureSchema source)
    {
        RejectIdentityField(request);
        if (source.IndexOf(MemorySchema.AutoIdentityColumn) >= 0)
        {
            throw SpatialException.BadArguments(
                $"The upload already has a field named '{MemorySchema.AutoIdentityColumn}'; use Identity=Source with an explicit IdentityField.");
        }

        var withId = new FeatureSchema(
            source.Fields.Append(new FieldDefinition(MemorySchema.AutoIdentityColumn, AttributeKind.Int64)));
        return (IngestIdentity.Auto, MemorySchema.AutoIdentityColumn, withId);
    }

    private static (IngestIdentity, string, FeatureSchema) Source(IngestRequest request, FeatureSchema source)
    {
        if (string.IsNullOrWhiteSpace(request.IdentityField))
        {
            throw SpatialException.BadArguments("Identity=Source requires an IdentityField name.");
        }

        var index = source.IndexOf(request.IdentityField);
        if (index < 0)
        {
            throw SpatialException.BadArguments(
                $"The identity field '{request.IdentityField}' is not a field of the ingest schema.");
        }

        if (source[index].Kind != AttributeKind.Int64)
        {
            throw SpatialException.BadArguments(
                $"The identity field '{request.IdentityField}' must be Int64 but the ingest schema declares {source[index].Kind}.");
        }

        return (IngestIdentity.Source, request.IdentityField, source);
    }

    private static void RejectIdentityField(IngestRequest request)
    {
        if (request.IdentityField is not null)
        {
            throw SpatialException.BadArguments("IdentityField is only used when Identity is Source.");
        }
    }
}
