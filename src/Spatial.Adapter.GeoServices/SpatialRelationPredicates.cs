using Spatial.Contracts;
using Spatial.Core.Geometry;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The exact DE-9IM definition of the served <c>spatialRel</c> verbs
/// (spec §9.1.4), expressed as intersection patterns over
/// <see cref="IGeometryRelations.Relate"/> — the engine's exact DE-9IM verb
/// (ADR-0036), not envelope arithmetic. Each verb keeps its envelope test as
/// a cheap necessary-condition pre-filter, so the exact predicate runs only
/// on candidates whose envelopes already agree.
///
/// The matrix rows are the feature geometry's components and the columns the
/// query geometry's, each in interior/boundary/exterior order, so
/// position 1 is interior∩interior, position 2 boundary-of-feature ∩
/// interior-of-query and position 4 interior-of-feature ∩
/// boundary-of-query. The patterns, with the geometry types they hold for:
///
/// | Verb | Pattern | Reading |
/// | --- | --- | --- |
/// | <c>Contains</c> | <c>T*****FF*</c> | the interiors meet and the query's exterior reaches neither the feature's interior nor its boundary — so a containee lying *on* the container's boundary is not contained, and a containee sharing part of the boundary still is. |
/// | <c>Within</c> | <c>T*F**F***</c> | the mirror, evaluated with the feature in the query's frame. |
/// | <c>Touches</c> | <c>F***T****</c> | interiors disjoint, boundaries meet — the equal-polygon, edge-sharing and collinear-line cases. |
/// | <c>Touches</c> (a point is involved) | <c>F**T*****</c> | interiors disjoint and the point sits on the other geometry's boundary: a point's own boundary is empty, so the meeting shows up in position 4 instead of position 5. |
/// | <c>Overlaps</c> | <c>T*T***T**</c> | interiors meet, each boundary reaches the other interior, and the exteriors meet — a genuine partial overlap, never containment. |
/// | <c>Crosses</c> | <c>T**T*****</c> / <c>T*T******</c> | interiors meet and the lower-dimensional geometry's interior reaches the higher-dimensional one's boundary; the pattern is selected by which side is lower-dimensional (position 4 or position 2). |
///
/// <c>Overlaps</c> and <c>Crosses</c> are equal- and mixed-dimension
/// constrained, so both are gated on the geometry dimensions: the patterns
/// alone would let a line crossing a polygon read as both <c>Overlaps</c>
/// and <c>Crosses</c> (OGC splits them by dimension).
/// </summary>
internal static class SpatialRelationPredicates
{
    private const string ContainsPattern = "T*****FF*";
    private const string WithinPattern = "T*F**F***";
    private const string TouchesPattern = "F***T****";
    private const string TouchesPointPattern = "F**T*****";
    private const string OverlapsPattern = "T*T***T**";
    private const string CrossesFeatureHigherPattern = "T**T*****";
    private const string CrossesFeatureLowerPattern = "T*T******";

    /// <summary>The feature geometry contains the query geometry (boundary-exact).</summary>
    internal static bool Contains(GeometryPair pair, IGeometryRelations relations, CancellationToken cancellationToken) =>
        pair.FeatureEnvelope.Contains(pair.QueryEnvelope)
        && relations.Relate(pair.Feature, pair.Query, ContainsPattern, cancellationToken);

    /// <summary>The feature geometry lies within the query geometry (boundary-exact).</summary>
    internal static bool Within(GeometryPair pair, IGeometryRelations relations, CancellationToken cancellationToken) =>
        pair.QueryEnvelope.Contains(pair.FeatureEnvelope)
        && relations.Relate(pair.Feature, pair.Query, WithinPattern, cancellationToken);

    /// <summary>The two geometries meet on their boundaries only, interiors disjoint.</summary>
    internal static bool Touches(GeometryPair pair, IGeometryRelations relations, CancellationToken cancellationToken) =>
        TouchPattern(pair) is { } pattern
        && relations.Relate(pair.Feature, pair.Query, pattern, cancellationToken);

    /// <summary>Same-dimension partial overlap: interiors meet, neither geometry contains the other.</summary>
    internal static bool Overlaps(GeometryPair pair, IGeometryRelations relations, CancellationToken cancellationToken) =>
        pair.EnvelopesIntersect()
        && SameDimension(pair)
        && relations.Relate(pair.Feature, pair.Query, OverlapsPattern, cancellationToken);

    /// <summary>Mixed-dimension partial overlap: interiors meet at the lower dimension, neither contains the other.</summary>
    internal static bool Crosses(GeometryPair pair, IGeometryRelations relations, CancellationToken cancellationToken) =>
        CrossesPattern(pair) is { } pattern
        && relations.Relate(pair.Feature, pair.Query, pattern, cancellationToken);

    /// <summary>
    /// The touches pattern for the pair, or <c>null</c> when the envelopes
    /// are disjoint: a point's own boundary is empty, so a touch involving a
    /// point shows up in position 4 (the point on the other's boundary)
    /// rather than in the boundaries-meet position 5.
    /// </summary>
    private static string? TouchPattern(GeometryPair pair) =>
        pair.EnvelopesIntersect() ? PointInvolved(pair) ? TouchesPointPattern : TouchesPattern : null;

    /// <summary>
    /// The crosses pattern for the pair, or <c>null</c> when the pair is
    /// disjoint, same-dimension (crosses is a mixed-dimension relation) or
    /// carries a geometry type with no topological dimension.
    /// </summary>
    private static string? CrossesPattern(GeometryPair pair)
    {
        if (!pair.EnvelopesIntersect())
        {
            return null;
        }

        var feature = Dimension(pair.Feature);
        var query = Dimension(pair.Query);
        return feature > query ? CrossesFeatureHigherPattern
            : query > feature ? CrossesFeatureLowerPattern
            : null;
    }

    private static bool SameDimension(GeometryPair pair) =>
        Dimension(pair.Feature) == Dimension(pair.Query);

    private static bool PointInvolved(GeometryPair pair) =>
        Dimension(pair.Feature) is 0 || Dimension(pair.Query) is 0;

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
}

/// <summary>
/// The geometry pair under test: the feature's geometry and the query
/// geometry with both envelopes. Threading one value instead of four
/// positional geometries keeps the match steps readable.
/// </summary>
internal readonly record struct GeometryPair(IGeometry Feature, Envelope FeatureEnvelope, IGeometry Query, Envelope QueryEnvelope)
{
    /// <summary>The pair when both geometries carry an envelope; null when either is missing or null.</summary>
    public static GeometryPair? Of(IGeometry? feature, IGeometry query) =>
        feature?.Envelope is { } featureEnvelope && query.Envelope is { } queryEnvelope
            ? new GeometryPair(feature, featureEnvelope, query, queryEnvelope)
            : null;

    /// <summary>
    /// The cheap necessary condition every intersecting or touching relation
    /// needs: disjoint envelopes cannot relate.
    /// </summary>
    public bool EnvelopesIntersect() => FeatureEnvelope.Intersects(QueryEnvelope);
}
