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
            if (Matches(new MatchCandidate(spec.Query, feature, objectId, spec.QueryGeometry, spec.Operations, uniqueId), cancellationToken))
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
        || SpatialMatch(match.Feature, match.QueryGeometry, match.Query.SpatialRel, match.Operations, cancellationToken);

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

    private static bool SpatialMatch(Feature feature, IGeometry queryGeometry, string spatialRel, IGeometryOperations operations, CancellationToken cancellationToken)
    {
        if (GeometryPair.Of(FeatureGeometry.Find(feature), queryGeometry) is not { } pair)
        {
            return false;
        }

        if (TryMatchEnvelopeShortcut(pair, spatialRel, operations, cancellationToken) is { } shortcut)
        {
            return shortcut;
        }

        return MatchTopology(pair, spatialRel, operations, cancellationToken);
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

    /// <summary>Null/missing envelopes plus the two relations that need no topological verb.</summary>
    private static bool? TryMatchEnvelopeShortcut(
        GeometryPair pair, string spatialRel, IGeometryOperations operations, CancellationToken cancellationToken)
    {
        if (string.Equals(spatialRel, EsriFeatureQuery.EnvelopeIntersects, StringComparison.Ordinal))
        {
            return pair.FeatureEnvelope.Intersects(pair.QueryEnvelope);
        }

        if (string.Equals(spatialRel, EsriFeatureQuery.Intersects, StringComparison.Ordinal))
        {
            return !operations.Intersection(pair.Feature, pair.Query, cancellationToken).IsEmpty;
        }

        return null;
    }

    /// <summary>The relations approximated with envelope-prefiltered topological verbs.</summary>
    private static bool MatchTopology(
        GeometryPair pair, string spatialRel, IGeometryOperations operations, CancellationToken cancellationToken)
    {
        return spatialRel switch
        {
            var rel when string.Equals(rel, EsriFeatureQuery.Contains, StringComparison.Ordinal) =>
                Contains(pair.Feature, pair.Query, pair.FeatureEnvelope, pair.QueryEnvelope, operations, cancellationToken),
            var rel when string.Equals(rel, EsriFeatureQuery.Within, StringComparison.Ordinal) =>
                Contains(pair.Query, pair.Feature, pair.QueryEnvelope, pair.FeatureEnvelope, operations, cancellationToken),
            var rel when string.Equals(rel, EsriFeatureQuery.Touches, StringComparison.Ordinal) =>
                Touches(pair.Feature, pair.Query, pair.FeatureEnvelope, pair.QueryEnvelope, operations, cancellationToken),
            var rel when string.Equals(rel, EsriFeatureQuery.Overlaps, StringComparison.Ordinal) =>
                Overlaps(pair.Feature, pair.Query, pair.FeatureEnvelope, pair.QueryEnvelope, operations, cancellationToken),
            var rel when string.Equals(rel, EsriFeatureQuery.Crosses, StringComparison.Ordinal) =>
                Crosses(pair.Feature, pair.Query, pair.FeatureEnvelope, pair.QueryEnvelope, operations, cancellationToken),
            _ => throw GeoServicesErrors.Invalid($"spatialRel '{spatialRel}' is not supported."),
        };
    }

    /// <summary>
    /// DE-9IM contains approximated with the available verbs: the container
    /// envelope must contain the containee envelope and the intersection must
    /// cover the containee (envelope-equal). Boundary cases (containee on the
    /// container boundary) read as contained; exact boundary exclusion needs
    /// a boundary verb the engine does not expose.
    /// </summary>
    private static bool Contains(IGeometry container, IGeometry containee, Envelope containerEnvelope, Envelope containeeEnvelope, IGeometryOperations operations, CancellationToken cancellationToken)
    {
        if (!containerEnvelope.Contains(containeeEnvelope))
        {
            return false;
        }

        var intersection = operations.Intersection(container, containee, cancellationToken);
        return !intersection.IsEmpty && intersection.Envelope is { } envelope && EnvelopesEqual(envelope, containeeEnvelope);
    }

    private static bool Touches(IGeometry left, IGeometry right, Envelope leftEnvelope, Envelope rightEnvelope, IGeometryOperations operations, CancellationToken cancellationToken)
    {
        var envelope = SharedBoundary(left, right, operations, cancellationToken);
        if (envelope is null)
        {
            return false;
        }

        return IsDegenerate(envelope.Value)
            && !EitherContains(left, right, leftEnvelope, rightEnvelope, operations, cancellationToken);
    }

    /// <summary>The envelope of the intersection, or <c>null</c> when the geometries share no point at all.</summary>
    private static Envelope? SharedBoundary(IGeometry left, IGeometry right, IGeometryOperations operations, CancellationToken cancellationToken)
    {
        var intersection = operations.Intersection(left, right, cancellationToken);
        return intersection.IsEmpty ? null : intersection.Envelope;
    }

    private static bool EitherContains(IGeometry left, IGeometry right, Envelope leftEnvelope, Envelope rightEnvelope, IGeometryOperations operations, CancellationToken cancellationToken) =>
        Contains(left, right, leftEnvelope, rightEnvelope, operations, cancellationToken)
        || Contains(right, left, rightEnvelope, leftEnvelope, operations, cancellationToken);

    internal static bool Overlaps(IGeometry left, IGeometry right, Envelope leftEnvelope, Envelope rightEnvelope, IGeometryOperations operations, CancellationToken cancellationToken)
    {
        if (Dimension(left) != Dimension(right))
        {
            return false;
        }

        return SameDimensionOverlap(left, right, operations, cancellationToken)
            && NeitherContains(left, right, leftEnvelope, rightEnvelope, operations, cancellationToken);
    }

    /// <summary>Overlapping at the same dimension: the intersection is non-empty and keeps that dimension.</summary>
    private static bool SameDimensionOverlap(IGeometry left, IGeometry right, IGeometryOperations operations, CancellationToken cancellationToken)
    {
        var intersection = operations.Intersection(left, right, cancellationToken);
        return !intersection.IsEmpty && Dimension(intersection) == Dimension(left);
    }

    /// <summary>Overlaps exclude containment, in either direction.</summary>
    private static bool NeitherContains(IGeometry left, IGeometry right, Envelope leftEnvelope, Envelope rightEnvelope, IGeometryOperations operations, CancellationToken cancellationToken) =>
        !Contains(left, right, leftEnvelope, rightEnvelope, operations, cancellationToken)
        && !Contains(right, left, rightEnvelope, leftEnvelope, operations, cancellationToken);

    private static bool Crosses(IGeometry left, IGeometry right, Envelope leftEnvelope, Envelope rightEnvelope, IGeometryOperations operations, CancellationToken cancellationToken)
    {
        if (Dimension(left) == Dimension(right))
        {
            return false;
        }

        var intersection = operations.Intersection(left, right, cancellationToken);
        if (intersection.IsEmpty)
        {
            return false;
        }

        return !Contains(left, right, leftEnvelope, rightEnvelope, operations, cancellationToken)
            && !Contains(right, left, rightEnvelope, leftEnvelope, operations, cancellationToken);
    }

    /// <summary>The topological dimension of a geometry: points 0, lines 1, areas 2, anything else -1.</summary>
    private static readonly Dictionary<GeometryType, int> Dimensions = new()
    {
        [GeometryType.Point] = 0,
        [GeometryType.MultiPoint] = 0,
        [GeometryType.LineString] = 1,
        [GeometryType.MultiLineString] = 1,
        [GeometryType.Polygon] = 2,
        [GeometryType.MultiPolygon] = 2,
    };

    private static int Dimension(IGeometry geometry) => Dimensions.GetValueOrDefault(geometry.Type, -1);

    private static bool EnvelopesEqual(Envelope left, Envelope right) =>
        SameOrigin(left, right) && SameExtent(left, right);

    private static bool SameOrigin(Envelope left, Envelope right) => left.MinX == right.MinX && left.MinY == right.MinY;

    private static bool SameExtent(Envelope left, Envelope right) => left.MaxX == right.MaxX && left.MaxY == right.MaxY;

    private static bool IsDegenerate(Envelope envelope) => envelope.MinX == envelope.MaxX || envelope.MinY == envelope.MaxY;

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
        string? UniqueId = null);
}
