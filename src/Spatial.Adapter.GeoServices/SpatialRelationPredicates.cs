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
/// The patterns are the OGC Simple Features table read verbatim, the feature
/// geometry as the left operand of the matrix (ADR-0106): the mask for the
/// verb, keyed on the pair's dimension pair only for the two verbs OGC defines
/// per dimension pair (<c>Overlaps</c>, <c>Crosses</c>), and a dimension pair
/// OGC defines no mask over asks no pattern at all. One table serves both the
/// Feature Service query path and the Geometry Service <c>relation</c>
/// operation, so the two endpoints cannot answer a pair of geometries
/// differently.
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
/// | <c>Within</c> | <c>T*F**F***</c> | the mirror, evaluated with the feature in the query's frame — the transpose of <c>Contains</c>, so the two are one predicate (ADR-0106). |
/// | <c>Touches</c> | <c>FT*******</c> \| <c>F**T*****</c> \| <c>F***T****</c> | interiors disjoint, and the contact in position 2, 4 or 5: the OGC touches masks, unioned into one dimension-free predicate, so a point or line on the other geometry's boundary touches it whichever side of the matrix the boundary lands on. The union is not one nine-character pattern — <c>FT*******</c> alone drops the edge-sharing and boundary-meeting cases — so it costs up to three <see cref="IGeometryRelations.Relate"/> calls, as <c>Intersects</c> does for its four. |
/// | <c>Overlaps</c> | <c>T*T***T**</c> (A/A), <c>1*T***T**</c> (L/L) | interiors meet, each boundary reaches the other interior, and the exteriors meet — a genuine partial overlap, never containment. The line/line reading asks the interiors to meet in dimension one, so a shared span overlaps and a crossing does not. |
/// | <c>Crosses</c> | <c>T**T*****</c> (A/L), <c>T*T******</c> (L/A), <c>0********</c> (L/L) | interiors meet and the lower-dimensional geometry's interior reaches the higher-dimensional one's boundary; the pattern is selected by which side is lower-dimensional (position 4 or position 2). A point is never <c>Crosses</c> an area: its dimension pair names no pattern, so none is asked, and a point inside an area is <c>Within</c> it (ADR-0106). |
/// | <c>Intersects</c> | <c>T********</c> or <c>*T*******</c> or <c>***T*****</c> or <c>****T****</c> | the OGC intersect union: the geometries meet if the interiors meet, or either interior reaches the other's boundary, or the boundaries meet. A pair shares nothing exactly when all four positions are <c>F</c>.
///
/// <c>Overlaps</c> and <c>Crosses</c> are dimension-dependent, so both are
/// gated on the geometry dimensions: the patterns alone would let a line
/// crossing a polygon read as both <c>Overlaps</c> and <c>Crosses</c> (OGC
/// splits them by dimension). Their patterns are keyed by the pair's
/// dimension pair rather than chosen by "which side is higher", because the
/// line/line reading is not a mirror of the mixed-dimension one — it asks
/// for a dimension-zero interior intersection, which is what separates two
/// crossing lines (cross, not overlap) from two collinear lines sharing a
/// span (overlap, not cross). A pair whose dimensions the reference does not
/// relate at all reads false, which is how a point pair is never an overlap
/// or a cross.
///
/// <c>Intersects</c> is the one verb whose definition is a disjunction, so it
/// costs four <see cref="IGeometryRelations.Relate"/> calls rather than one:
/// a DE-9IM pattern is a single nine-character matrix and the grammar takes
/// no alternation, so a <c>|</c>-joined pattern is rejected outright. The
/// disjunction short-circuits, and the envelope test rejects a disjoint pair
/// before any of them run.
/// </summary>
internal static class SpatialRelationPredicates
{
    private const string ContainsPattern = "T*****FF*";
    private const string WithinPattern = "T*F**F***";
    private const string OverlapsSurfacePattern = "T*T***T**";
    private const string OverlapsCurvePattern = "1*T***T**";
    private const string CrossesFeatureSurfacePattern = "T**T*****";
    private const string CrossesFeatureCurvePattern = "T*T******";
    private const string CrossesCurvePattern = "0********";

    /// <summary>
    /// The OGC touches masks, in the order they are tried: the feature's
    /// boundary reaching the query's interior (position 2), the query's
    /// interior reaching the feature's boundary (position 4), or the two
    /// boundaries meeting (position 5) — each with disjoint interiors. The
    /// union is the dimension-keyed JTS rule flattened: JTS selects one of
    /// the three by geometry dimension, which is how a point or line
    /// feature on a query polygon's boundary fell out of the pattern table
    /// (SpatialEngine-u2x.35) — the contact lands in position 2 for a point
    /// (whose own boundary is empty) and in position 4 for a line along the
    /// edge, and neither of the two masks the served table carried named
    /// position 2. No single nine-character pattern states the union, so the
    /// disjunction costs up to three <see cref="IGeometryRelations.Relate"/>
    /// calls, as <c>Intersects</c> does for its four.
    /// </summary>
    private static readonly string[] TouchesPatterns =
    [
        "FT*******",
        "F**T*****",
        "F***T****",
    ];

    /// <summary>
    /// The OGC intersect patterns, in the order they are tried: the interiors
    /// meet, either interior reaches the other's boundary, or the boundaries
    /// meet. A pair with nothing in common has all four positions false.
    /// </summary>
    private static readonly string[] IntersectsPatterns =
    [
        "T********",
        "*T*******",
        "***T*****",
        "****T****",
    ];

    /// <summary>The feature geometry contains the query geometry (boundary-exact).</summary>
    internal static bool Contains(GeometryPair pair, IGeometryRelations relations, CancellationToken cancellationToken) =>
        pair.FeatureEnvelope.Contains(pair.QueryEnvelope)
        && relations.Relate(pair.Feature, pair.Query, ContainsPattern, cancellationToken);

    /// <summary>The feature geometry is within the query geometry: the transpose of <see cref="Contains"/>, spelled in the feature's frame (ADR-0106).</summary>
    internal static bool Within(GeometryPair pair, IGeometryRelations relations, CancellationToken cancellationToken) =>
        pair.QueryEnvelope.Contains(pair.FeatureEnvelope)
        && relations.Relate(pair.Feature, pair.Query, WithinPattern, cancellationToken);

    /// <summary>
    /// The two geometries meet on their boundaries only, interiors disjoint —
    /// the OGC touches masks as one dimension-free union.
    /// </summary>
    internal static bool Touches(GeometryPair pair, IGeometryRelations relations, CancellationToken cancellationToken) =>
        pair.EnvelopesIntersect()
        && TouchesPatterns.Any(pattern => relations.Relate(pair.Feature, pair.Query, pattern, cancellationToken));

    /// <summary>
    /// The two geometries meet, as the intersection <em>test</em> over the
    /// OGC intersect patterns rather than as a non-empty intersection
    /// <em>geometry</em>: this runs per candidate feature, so materialising
    /// the intersection costs an overlay per row to answer a question the
    /// pattern table already answers (SpatialEngine-51k).
    /// </summary>
    internal static bool Intersects(GeometryPair pair, IGeometryRelations relations, CancellationToken cancellationToken) =>
        pair.EnvelopesIntersect()
        && IntersectsPatterns.Any(pattern => relations.Relate(pair.Feature, pair.Query, pattern, cancellationToken));

    /// <summary>Same-dimension partial overlap: interiors meet in the pair's own dimension, neither geometry contains the other.</summary>
    internal static bool Overlaps(GeometryPair pair, IGeometryRelations relations, CancellationToken cancellationToken) =>
        pair.EnvelopesIntersect()
        && OverlapsPattern(pair) is { } pattern
        && relations.Relate(pair.Feature, pair.Query, pattern, cancellationToken);

    /// <summary>
    /// Crosses: the interiors meet and, where one geometry is
    /// lower-dimensional, its interior reaches the other's boundary.
    /// </summary>
    internal static bool Crosses(GeometryPair pair, IGeometryRelations relations, CancellationToken cancellationToken) =>
        CrossesPattern(pair) is { } pattern
        && relations.Relate(pair.Feature, pair.Query, pattern, cancellationToken);

    /// <summary>
    /// The <c>Overlaps</c> pattern for the pair's dimension pair, or
    /// <c>null</c> when the reference states none — a mixed-dimension pair
    /// never overlaps, and neither does a pair of points.
    ///
    /// Both readings ask the exteriors to meet and neither geometry to
    /// contain the other, and they differ only in how far the interiors have
    /// to agree. A surface/surface pair needs the interiors to meet
    /// (<c>T</c>); a line/line pair needs them to meet *in dimension one*
    /// (<c>1</c>), because two lines crossing at a point share a point and
    /// not a span. Reading the line/line pair with the surface pattern is
    /// what let a crossing pair answer true (SpatialEngine-u2x.56).
    /// </summary>
    private static string? OverlapsPattern(GeometryPair pair) =>
        (Dimension(pair.Feature), Dimension(pair.Query)) switch
        {
            (2, 2) => OverlapsSurfacePattern,
            (1, 1) => OverlapsCurvePattern,
            _ => null,
        };

    /// <summary>
    /// The <c>Crosses</c> pattern for the pair's dimension pair, or
    /// <c>null</c> when the reference states none — a pair of equal
    /// surfaces, and any pair involving a point, has no pattern to ask, so a
    /// point inside an area is <c>Within</c> it and never <c>Crosses</c> it
    /// (ADR-0106). The discarded dimension-masked reading asked
    /// <c>0********</c> for that pair and answered true; see that record for
    /// the measurement.
    ///
    /// The two mixed-dimension readings are the mirror images of one
    /// another — the lower-dimensional geometry's interior has to reach the
    /// higher-dimensional one's boundary, which is position 4 when the
    /// feature is the surface and position 2 when it is the curve. The
    /// line/line reading is not a mirror of either: two lines cross when
    /// their interiors meet in a point (<c>0********</c>), and a collinear
    /// pair that shares a span does not, because its interiors meet in
    /// dimension one (SpatialEngine-u2x.56).
    /// </summary>
    private static string? CrossesPattern(GeometryPair pair)
    {
        if (!pair.EnvelopesIntersect())
        {
            return null;
        }

        return (Dimension(pair.Feature), Dimension(pair.Query)) switch
        {
            (2, 1) => CrossesFeatureSurfacePattern,
            (1, 2) => CrossesFeatureCurvePattern,
            (1, 1) => CrossesCurvePattern,
            _ => null,
        };
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
