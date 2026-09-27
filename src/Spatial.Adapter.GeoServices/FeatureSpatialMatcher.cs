using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The feature-match predicate (spec §9.1.4): id, unique-id, where-clause,
/// time-window and spatial matching plus the envelope-prefiltered exact
/// <c>spatialRel</c> predicates. Split out of <see cref="FeatureQueryEngine"/> so the
/// query facade keeps only orchestration and the match fan-out (filter,
/// time, geometry verbs) lives with the code that uses it (ADR-0040).
/// Shared by the Feature Service match loop and the Image Service catalog
/// query, so both agree on what "matches" means.
/// </summary>
internal static class FeatureSpatialMatcher
{
    internal static async Task<List<MatchedFeature>> MatchAsync(QuerySpec spec, CancellationToken cancellationToken)
    {
        var batches = await spec.Store.ScanAsync(spec.Dataset.Id, cancellationToken);
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
            if (Matches(new MatchCandidate(spec.Query, feature, objectId, spec.QueryGeometry, spec.Operations, spec.Relations, uniqueId), cancellationToken))
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

    private static bool MatchesWhere(MatchCandidate match) =>
        match.Query.Where is not { } where
        || where.Matches(match.Feature, SyntheticObjectId(match.ObjectId));

    private static bool MatchesTimeWindow(MatchCandidate match) =>
        match.Query.Time is not { } time || MatchesTime(match.Feature, time);

    private static bool MatchesSpatial(MatchCandidate match, CancellationToken cancellationToken) =>
        match.QueryGeometry is null
        || SpatialMatch(match.Feature, match.QueryGeometry, match.Query.SpatialRel, match.Operations, match.Relations, cancellationToken);

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

    /// <summary>The synthetic <c>OBJECTID</c> a where clause may reference (ADR-0037).</summary>
    private static EsriSyntheticField SyntheticObjectId(long objectId) =>
        new(EsriLayerModel.ObjectIdField, AttributeValue.FromInt64(objectId));

    private static bool SpatialMatch(
        Feature feature,
        IGeometry queryGeometry,
        string spatialRel,
        IGeometryOperations operations,
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

        if (string.Equals(spatialRel, EsriFeatureQuery.Intersects, StringComparison.Ordinal))
        {
            return !operations.Intersection(pair.Feature, pair.Query, cancellationToken).IsEmpty;
        }

        return MatchTopology(pair, spatialRel, relations, cancellationToken);
    }

    /// <summary>
    /// The geometry pair under test: the feature's geometry and the query
    /// geometry with both envelopes. Threading one value instead of four
    /// positional geometries keeps the match steps readable.
    /// </summary>
    private readonly record struct GeometryPair(IGeometry Feature, Envelope FeatureEnvelope, IGeometry Query, Envelope QueryEnvelope)
    {
        /// <summary>The pair when both geometries carry an envelope; null when either is missing or null.</summary>
        public static GeometryPair? Of(IGeometry? feature, IGeometry query) =>
            feature?.Envelope is { } featureEnvelope && query.Envelope is { } queryEnvelope
                ? new GeometryPair(feature, featureEnvelope, query, queryEnvelope)
                : null;
    }

    /// <summary>
    /// The exact relations, each behind its envelope pre-filter — the cheap
    /// test a containee whose envelope escapes the container's cannot pass,
    /// and a pair with disjoint envelopes cannot touch, overlap or cross — so
    /// the exact DE-9IM predicate only runs on the candidates that survive.
    /// </summary>
    private static bool MatchTopology(
        GeometryPair pair, string spatialRel, IGeometryRelations relations, CancellationToken cancellationToken) =>
        spatialRel switch
        {
            var rel when string.Equals(rel, EsriFeatureQuery.Contains, StringComparison.Ordinal) =>
                pair.FeatureEnvelope.Contains(pair.QueryEnvelope)
                && SpatialRelationPredicates.Contains(relations, pair.Feature, pair.Query, cancellationToken),
            var rel when string.Equals(rel, EsriFeatureQuery.Within, StringComparison.Ordinal) =>
                pair.QueryEnvelope.Contains(pair.FeatureEnvelope)
                && SpatialRelationPredicates.Contains(relations, pair.Query, pair.Feature, cancellationToken),
            var rel when string.Equals(rel, EsriFeatureQuery.Touches, StringComparison.Ordinal) =>
                Overlapping(pair)
                && SpatialRelationPredicates.Touches(relations, pair.Feature, pair.Query, cancellationToken),
            var rel when string.Equals(rel, EsriFeatureQuery.Overlaps, StringComparison.Ordinal) =>
                Overlapping(pair)
                && SpatialRelationPredicates.Overlaps(relations, pair.Feature, pair.Query, cancellationToken),
            var rel when string.Equals(rel, EsriFeatureQuery.Crosses, StringComparison.Ordinal) =>
                Overlapping(pair)
                && SpatialRelationPredicates.Crosses(relations, pair.Feature, pair.Query, cancellationToken),
            _ => throw GeoServicesErrors.Invalid($"spatialRel '{spatialRel}' is not supported."),
        };

    /// <summary>The pre-filter for the relations a disjoint envelope pair cannot satisfy.</summary>
    private static bool Overlapping(GeometryPair pair) => pair.FeatureEnvelope.Intersects(pair.QueryEnvelope);

    /// <summary>
    /// One feature-match invocation: which layer, store and parsed query to
    /// match, with the pre-transformed query geometry and the object-id
    /// scheme the scan ordinals resolve through. Threading one value instead
    /// of seven parameters keeps the match loop readable (metrics
    /// long-parameter-list).
    /// </summary>
    internal sealed record QuerySpec(
        DatasetDescription Dataset,
        IFeatureStore Store,
        EsriFeatureQuery Query,
        IGeometry? QueryGeometry,
        IGeometryOperations Operations,
        IGeometryRelations Relations,
        EsriObjectIdScheme Scheme);

    /// <summary>
    /// One per-feature match candidate: the parsed query, the feature and
    /// its resolved <c>OBJECTID</c>, the pre-transformed query geometry and
    /// the geometry verbs a spatial predicate needs. Shared by the Feature
    /// Service match loop and the Image Service catalog query, so both agree
    /// on what "matches" means.
    /// </summary>
    internal sealed record MatchCandidate(
        EsriFeatureQuery Query,
        Feature Feature,
        long ObjectId,
        IGeometry? QueryGeometry,
        IGeometryOperations Operations,
        IGeometryRelations Relations,
        string? UniqueId = null);
}
