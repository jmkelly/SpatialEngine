using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// Serves <c>queryRelatedRecords</c> (spec §9.1.5) over a declared
/// relationship (ADR-0077). The traversal is the ordinary query path pointed
/// at a different layer: the declaration names the two key columns, the
/// origin record's key becomes an equality term in the closed where-grammar,
/// and the related layer is matched, ordered and projected exactly as
/// <c>query</c> does — so the related layer's own <c>where</c>,
/// <c>outFields</c>, <c>geometry</c>/<c>spatialRel</c> and <c>outSR</c> all
/// apply. A many-to-many relationship reads its join rows first and
/// disjoins the keys they name. Nothing here is store-specific: the
/// declaration is publication state and the reads are the same feature-read
/// faces every layer uses.
/// </summary>
internal static class FeatureRelationshipEngine
{
    /// <summary>Executes <c>queryRelatedRecords</c> and writes the spec §9.1.5.6 response.</summary>
    public static async Task<IResult> QueryRelatedRecordsAsync(
        QueryRequest request,
        int layerId,
        IGeometryOperations operations,
        IGeometryRelations relations,
        ICoordinateTransforms transforms,
        CancellationToken cancellationToken)
    {
        var parameters = await LayerQuery.ReadParameters(request);
        var target = await FeatureRelationshipTargets.ResolveAsync(
            request.Catalog, request.Registry, request.Service, layerId, parameters.Get("relationshipId"), request.Stores, cancellationToken);
        var query = RelatedQuery.Parse(parameters, target.Related.Description);
        var traversal = new Traversal(target, query, request.Stores, operations, relations, transforms, cancellationToken);
        var relatedRows = await ScanRelatedAsync(traversal);
        var groups = new List<RelatedGroup>();

        foreach (var origin in await OriginTargetsAsync(traversal, parameters))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = FeatureRelationshipTargets.KeyValue(target.Origin, target.Relationship, origin.Feature);
            if (key is null)
            {
                continue;
            }

            var rows = await RelatedForAsync(traversal, relatedRows, origin.Feature, key.Value);
            if (rows.Count > 0)
            {
                groups.Add(new RelatedGroup(target.Relationship.Name, key.Value, rows));
            }
        }

        return Write(target.Related.Description, query, groups, cancellationToken);
    }

    /// <summary>
    /// The related layer's records with their object ids resolved once, so a
    /// traversal over many origin records scans the related store once rather
    /// than once per origin feature.
    /// </summary>
    private static async Task<List<MatchedFeature>> ScanRelatedAsync(Traversal traversal)
    {
        var dataset = traversal.Target.Related.Description;
        var scheme = EsriObjectIdScheme.For(dataset);
        var rows = new List<MatchedFeature>();
        long ordinal = 0;
        foreach (var feature in (await traversal.Target.RelatedStore.ScanAsync(dataset.Id, traversal.CancellationToken))
            .SelectMany(batch => batch.Features))
        {
            traversal.CancellationToken.ThrowIfCancellationRequested();
            ordinal++;
            if (!scheme.TryResolve(feature, ordinal, out var objectId))
            {
                throw GeoServicesErrors.ServerError(
                    $"The identity column of layer '{dataset.Id}' is not an integer.");
            }

            rows.Add(new MatchedFeature(objectId, feature));
        }

        return rows;
    }

    /// <summary>
    /// The origin records the request addresses: the requested object ids in
    /// request order, or every record in scan order. Unknown ids are
    /// <c>not.found</c>, never silently dropped.
    /// </summary>
    private static Task<IReadOnlyList<(long ObjectId, Feature Feature)>> OriginTargetsAsync(
        Traversal traversal, EsriRequestParameters parameters) =>
        FeatureAttachmentTargets.ResolveAsync(
            traversal.Target.Origin,
            traversal.Target.OriginStore,
            FeatureAttachmentTargets.ParseIds(parameters.Get("objectIds"), "objectIds"),
            traversal.CancellationToken);

    /// <summary>
    /// The related rows of one origin record: the key term (or, for a
    /// many-to-many relationship, the disjunction of the keys its join rows
    /// name) ANDed with the related layer's own <c>where</c>, matched,
    /// ordered and projected by the shared query machinery.
    /// </summary>
    private static async Task<List<MatchedFeature>> RelatedForAsync(
        Traversal traversal, List<MatchedFeature> relatedRows, Feature originFeature, AttributeValue key)
    {
        var cancellationToken = traversal.CancellationToken;
        var term = await KeyTermAsync(traversal, originFeature, key);
        var effective = traversal.Query.Query with
        {
            Where = traversal.Query.Query.Where is { } requested ? term.And(requested) : term,
        };
        return RelatedMatch.Apply(
            effective,
            relatedRows,
            traversal.Target.Related.Description,
            traversal.Query.LayerCrs,
            traversal.Operations,
            traversal.Relations,
            traversal.Transforms,
            cancellationToken);
    }

    /// <summary>
    /// The key term for one origin record: the single equality of a
    /// one-to-one/one-to-many declaration, or the disjunction over the keys
    /// its join rows name for a many-to-many one. An origin record the join
    /// does not reach answers the constant false term, never every record.
    /// </summary>
    private static async Task<EsriWhere> KeyTermAsync(
        Traversal traversal, Feature originFeature, AttributeValue key)
    {
        var relationship = traversal.Target.Relationship;
        if (relationship.Join is not { } join)
        {
            return FeatureRelationshipKeys.Parse(FeatureRelationshipKeys.Equality(relationship.RelatedKeyColumn, key)!);
        }

        var rows = await FeatureRelationshipTargets.JoinRowsAsync(traversal.Target, traversal.Stores, originFeature, traversal.CancellationToken);
        return FeatureRelationshipKeys.Parse(FeatureRelationshipKeys.Disjoin(
            rows.Rows.Select(row => FeatureRelationshipKeys.Equality(
                relationship.RelatedKeyColumn,
                JoinKeyValue(rows.Description, join.RelatedKeyColumn, row)))));
    }

    private static AttributeValue JoinKeyValue(DatasetDescription join, string column, Feature row)
    {
        var index = join.Schema.IndexOf(column);
        return index < 0
            ? throw GeoServicesErrors.Invalid($"The join dataset '{join.Id}' has no column '{column}'.")
            : row[index];
    }

    /// <summary>
    /// The §9.1.5.6 response: the related layer's field metadata and one entry
    /// per origin record that has related records, each carrying the key the
    /// rows relate through and the projected rows. Origin records with no
    /// related records are omitted rather than served empty.
    /// </summary>
    private static IResult Write(
        DatasetDescription related,
        RelatedQuery query,
        IReadOnlyList<RelatedGroup> groups,
        CancellationToken cancellationToken) =>
        FeatureResponseWriter.WriteRelatedGroups(related, query, groups, cancellationToken);

    /// <summary>
    /// One traversal's fixed state: the resolved relationship, the related
    /// layer's query, the store registry the join dataset resolves through and
    /// the geometry verb faces the shared match needs. Threading one value
    /// instead of nine parameters keeps the traversal methods to what they
    /// actually vary on.
    /// </summary>
    private sealed record Traversal(
        FeatureRelationshipTargets.RelationshipTarget Target,
        RelatedQuery Query,
        IStoreRegistry Stores,
        IGeometryOperations Operations,
        IGeometryRelations Relations,
        ICoordinateTransforms Transforms,
        CancellationToken CancellationToken);
}

/// <summary>One origin record's related rows, with the key they relate through.</summary>
internal sealed record RelatedGroup(string Name, AttributeValue RelatedKey, IReadOnlyList<MatchedFeature> Rows);

/// <summary>
/// One traversal's match→order→project pass: the related rows of one origin
/// record, matched by the shared query machinery, ordered and projected
/// exactly as <c>query</c> does. Split out of
/// <see cref="FeatureRelationshipEngine"/> so the traversal keeps only
/// orchestration and the match fan-out lives with the code that uses it.
/// </summary>
internal static class RelatedMatch
{
    internal static List<MatchedFeature> Apply(
        EsriFeatureQuery effective,
        IReadOnlyList<MatchedFeature> relatedRows,
        DatasetDescription related,
        CoordinateReference? layerCrs,
        IGeometryOperations operations,
        IGeometryRelations relations,
        ICoordinateTransforms transforms,
        CancellationToken cancellationToken)
    {
        var queryGeometry = FeatureProjection.TransformQueryGeometry(
            effective.Geometry, layerCrs, transforms, cancellationToken);
        var matches = MatchRows(effective, relatedRows, queryGeometry, related, relations, cancellationToken);
        var ordered = FeatureOrdering.Apply(matches, FeatureOrdering.Compile(related, effective));
        return ProjectRows(ordered, effective, layerCrs, operations, transforms, cancellationToken);
    }

    private static List<MatchedFeature> MatchRows(
        EsriFeatureQuery effective,
        IReadOnlyList<MatchedFeature> relatedRows,
        IGeometry? queryGeometry,
        DatasetDescription related,
        IGeometryRelations relations,
        CancellationToken cancellationToken) =>
        relatedRows
            .Where(row => FeatureSpatialMatcher.Matches(
                new FeatureSpatialMatcher.MatchCandidate(
                    effective, row.Feature, row.ObjectId, queryGeometry, relations,
                    TimeFields: related.TimeFields),
                cancellationToken))
            .ToList();

    private static List<MatchedFeature> ProjectRows(
        List<MatchedFeature> ordered,
        EsriFeatureQuery effective,
        CoordinateReference? layerCrs,
        IGeometryOperations operations,
        ICoordinateTransforms transforms,
        CancellationToken cancellationToken) =>
        ordered
            .Select(row => FeatureProjection.TransformFeature(row, effective, layerCrs, transforms, operations, cancellationToken))
            .ToList();
}
