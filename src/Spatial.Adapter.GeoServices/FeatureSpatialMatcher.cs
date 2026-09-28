using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The feature-match predicate (spec §9.1.4): id, unique-id, where-clause,
/// time-window and spatial matching plus the envelope-prefiltered
/// topological verbs. Split out of <see cref="FeatureQueryEngine"/> so the
/// query facade keeps only orchestration and the match fan-out (filter,
/// time, geometry verbs) lives with the code that uses it (ADR-0040).
/// Shared by the Feature Service match loop and the Image Service catalog
/// query, so both agree on what "matches" means.
/// </summary>
internal static class FeatureSpatialMatcher
{
    /// <summary>
    /// The features that match the query. The attribute <c>where</c> clause is
    /// handed to the store as a predicate (ADR-0074 §7) so the filter is
    /// answered by the provider — pushed down to SQL where the store has a
    /// dialect for it, evaluated in memory where it has not — instead of
    /// being tested feature by feature here. The facets a store cannot
    /// express (ids, unique ids, <c>time</c>, the topology verbs) stay
    /// per-feature matches on the rows the store returned.
    /// </summary>
    internal static async Task<List<MatchedFeature>> MatchAsync(QuerySpec spec, CancellationToken cancellationToken)
    {
        var pushdown = EsriWhereResolver.Pushdown(spec.Query.Where, spec.Scheme, spec.Dataset);
        var batches = pushdown is null
            ? await spec.Store.ScanAsync(spec.Dataset.Id, cancellationToken)
            : (await spec.Store.QueryAsync(
                spec.Dataset.Id, new FeatureQuery(Where: pushdown), cancellationToken)).Batches;
        var matches = new List<MatchedFeature>();
        long ordinal = 0;
        foreach (var feature in batches.SelectMany(batch => batch.Features))
        {
            ordinal++;
            if (!spec.Scheme.TryResolve(feature, ordinal, out var objectId))
            {
                throw GeoServicesErrors.ServerError(
                    $"The identity column of layer '{spec.Dataset.Id}' is not an integer.");
            }

            var uniqueId = EsriUniqueIdScheme.ResolveFor(spec.Query, spec.Dataset, feature);
            // The clause reached the store as the plan's predicate, so the
            // facade does not test it again here — and must not, or a store
            // that answered a different row set would look right (ADR-0097).
            var candidate = new MatchCandidate(
                spec.Query, feature, objectId, spec.QueryGeometry, spec.Services.Relations, uniqueId,
                WherePushedDown: pushdown is not null);
            if (Matches(candidate, cancellationToken))
            {
                matches.Add(new MatchedFeature(objectId, feature));
            }
        }

        return matches;
    }

    internal static bool Matches(MatchCandidate match, CancellationToken cancellationToken)
    {
        if (!MatchesIds(match))
        {
            return false;
        }

        if (!MatchesUniqueIds(match))
        {
            return false;
        }

        if (!MatchesWhere(match))
        {
            return false;
        }

        if (!MatchesTimeWindow(match))
        {
            return false;
        }

        return MatchesSpatial(match, cancellationToken);
    }

    private static bool MatchesIds(MatchCandidate match) =>
        match.Query.ObjectIds is not { } ids || ids.Contains(match.ObjectId);

    // T-036/T-058: the string-ID filter (spec §9.1.4, 11.5+). A null unique id
    // never equals a requested id; layers without a string-or-guid unique-id
    // model are rejected when the match loops resolve the id.
    private static bool MatchesUniqueIds(MatchCandidate match) =>
        match.Query.UniqueIds is not { } wanted
        || (match.UniqueId is not null && wanted.Contains(match.UniqueId, StringComparer.Ordinal));

    /// <summary>
    /// The attribute clause. A store pushdown has already applied it; this is
    /// the residual case — a clause naming the synthetic <c>OBJECTID</c> of a
    /// layer with no integer identity column, which no store can read.
    /// </summary>
    private static bool MatchesWhere(MatchCandidate match) =>
        match.WherePushedDown
        || EsriPredicateEvaluator.Matches(
            match.Query.Where?.Predicate,
            match.Feature,
            new EsriFieldOverlay(EsriLayerModel.ObjectIdField, AttributeValue.FromInt64(match.ObjectId)));

    private static bool MatchesTimeWindow(MatchCandidate match) =>
        match.Query.Time is not { } time || MatchesTime(match.Feature, time);

    private static bool MatchesSpatial(MatchCandidate match, CancellationToken cancellationToken) =>
        match.QueryGeometry is null
        || SpatialMatch(match.Feature, match.QueryGeometry, match.Query.SpatialRel, match.Relations, cancellationToken);

    /// <summary>
    /// Applies the <c>time</c> extent to the feature's date attributes: the
    /// feature matches when any date value falls inside the (inclusive)
    /// bounds, where a <c>null</c> bound is infinite. A feature with no date
    /// values matches unconditionally — ArcGIS Server ignores <c>time</c> on
    /// layers without time-aware (date) fields. Shared with the MapServer
    /// identify path (T-059), which filters dated hits with the same rule.
    /// </summary>
    internal static bool MatchesTime(Feature feature, EsriTimeExtent time)
    {
        var dates = feature.Attributes
            .Where(attribute => attribute.Kind == AttributeKind.DateTimeOffset)
            .Select(attribute => attribute.DateTimeOffsetValue)
            .ToArray();
        return dates.Length == 0 || dates.Any(date => Within(date, time));
    }

    /// <summary>One date value against the inclusive bounds, where a null bound is infinite.</summary>
    private static bool Within(DateTimeOffset value, EsriTimeExtent time) =>
        value.ToUnixTimeMilliseconds() >= (time.StartMs ?? long.MinValue)
        && value.ToUnixTimeMilliseconds() <= (time.EndMs ?? long.MaxValue);

    private static bool SpatialMatch(
        Feature feature,
        IGeometry queryGeometry,
        string spatialRel,
        IGeometryRelations relations,
        CancellationToken cancellationToken)
    {
        if (GeometryPair.Of(FeatureGeometry.Find(feature), queryGeometry) is not { } pair)
        {
            return false;
        }

        if (string.Equals(spatialRel, EsriFeatureQuery.EnvelopeIntersects, StringComparison.Ordinal))
        {
            return pair.FeatureEnvelope.Intersects(pair.QueryEnvelope);
        }

        return MatchTopology(pair, spatialRel, relations, cancellationToken);
    }

    /// <summary>The relations approximated with envelope-prefiltered topological verbs.</summary>
    private static bool MatchTopology(
        GeometryPair pair, string spatialRel, IGeometryRelations relations, CancellationToken cancellationToken) =>
        spatialRel switch
        {
            var rel when string.Equals(rel, EsriFeatureQuery.Contains, StringComparison.Ordinal) =>
                SpatialRelationPredicates.Contains(pair, relations, cancellationToken),
            var rel when string.Equals(rel, EsriFeatureQuery.Within, StringComparison.Ordinal) =>
                SpatialRelationPredicates.Within(pair, relations, cancellationToken),
            var rel when string.Equals(rel, EsriFeatureQuery.Touches, StringComparison.Ordinal) =>
                SpatialRelationPredicates.Touches(pair, relations, cancellationToken),
            var rel when string.Equals(rel, EsriFeatureQuery.Overlaps, StringComparison.Ordinal) =>
                SpatialRelationPredicates.Overlaps(pair, relations, cancellationToken),
            var rel when string.Equals(rel, EsriFeatureQuery.Crosses, StringComparison.Ordinal) =>
                SpatialRelationPredicates.Crosses(pair, relations, cancellationToken),
            var rel when string.Equals(rel, EsriFeatureQuery.Intersects, StringComparison.Ordinal) =>
                SpatialRelationPredicates.Intersects(pair, relations, cancellationToken),
            _ => throw GeoServicesErrors.Invalid($"spatialRel '{spatialRel}' is not supported."),
        };

    /// <summary>
    /// One feature-match invocation: which layer, store and parsed query to
    /// match, with the pre-transformed query geometry, the object-id scheme
    /// the scan ordinals resolve through and the geometry verb faces the
    /// spatial predicates need. Threading one value instead of eight
    /// parameters keeps the match loop readable (metrics
    /// long-parameter-list).
    /// </summary>
    internal sealed record QuerySpec(
        DatasetDescription Dataset,
        IFeatureStore Store,
        EsriFeatureQuery Query,
        IGeometry? QueryGeometry,
        QueryServices Services,
        EsriObjectIdScheme Scheme);

    /// <summary>
    /// One per-feature match candidate: the parsed query, the feature and
    /// its resolved <c>OBJECTID</c>, the pre-transformed query geometry and
    /// the relation verb the spatial predicates need. Shared by the Feature
    /// Service match loop, the Image Service catalog query and the
    /// relationship traversal, so all three agree on what "matches" means.
    /// The verbs are reached from the query's own geometry, so a traversal
    /// resolves the relation face it holds rather than the whole
    /// <see cref="QueryServices"/> bundle the query path carries.
    /// </summary>
    internal sealed record MatchCandidate(
        EsriFeatureQuery Query,
        Feature Feature,
        long ObjectId,
        IGeometry? QueryGeometry,
        IGeometryRelations Relations,
        string? UniqueId = null,
        bool WherePushedDown = false);
}
