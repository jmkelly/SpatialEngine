using Spatial.Contracts;
using Spatial.Core.Geometry;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The OGC DE-9IM patterns behind the served <c>spatialRel</c> relations
/// (spec §9.1.4). Each relation is a set of intersection patterns over the
/// engine's <see cref="IGeometryRelations.Relate"/> verb, so a served
/// relation is the exact OGC predicate rather than an envelope
/// approximation. A pattern's dimension-dependent element is selected by the
/// pair's own topological dimensions, so a point never contains a polygon and
/// same-dimension pairs never cross. No geometry type crosses this boundary
/// and the adapter stays a mapping layer (ADR-0005).
/// </summary>
internal static class SpatialRelationPredicates
{
    /// <summary>
    /// Contains: the containee's points all lie in the container (its
    /// exterior meets neither the containee's interior nor its boundary) and
    /// the two interiors meet — the one pattern that covers area-in-area,
    /// line-in-area and point-in-area. A containee resting on the container's
    /// boundary is still contained, as OGC requires; a containee with points
    /// in the container's exterior (a query filling a hole) is not.
    /// </summary>
    private static readonly Dictionary<(int Left, int Right), string[]> ContainsPatterns = new()
    {
        [(2, 2)] = ["T*****FF*"],
        [(2, 1)] = ["T*****FF*"],
        [(2, 0)] = ["T*****FF*"],
        [(1, 0)] = ["T********"],
    };

    /// <summary>
    /// Touches: the interiors are disjoint and the pair shares a boundary
    /// point — the left interior on the right boundary (<c>F***T****</c>), a
    /// shared boundary segment (<c>F**T*****</c>), or the right interior on
    /// the left boundary (<c>FT*******</c>). Dimension-independent: every
    /// pattern requires disjoint interiors, so no pair of dimensions can
    /// over-match.
    /// </summary>
    private static readonly string[] TouchesPatterns = ["F***T****", "F**T*****", "FT*******"];

    /// <summary>
    /// Overlaps: same-dimension pairs whose interiors meet at that
    /// dimension and whose exteriors meet, so neither contains the other —
    /// the <c>II</c> element names the shared dimension and a pair of
    /// different dimension has no pattern.
    /// </summary>
    private static readonly Dictionary<(int Left, int Right), string[]> OverlapsPatterns = new()
    {
        [(2, 2)] = ["T*T***T**"],
        [(1, 1)] = ["1*T****T*"],
        [(0, 0)] = ["0*T****T*"],
    };

    /// <summary>
    /// Crosses: different-dimension pairs whose interiors meet while the
    /// smaller geometry is not contained in the larger — the area/line pair
    /// needs a part of the line beyond the area (<c>T*****T**</c> its
    /// interior, <c>T******T*</c> its boundary) and the line/point pair a
    /// point on the line (<c>T********</c>). A point against an area has no
    /// pattern: it is within the area, never crossing it.
    /// </summary>
    private static readonly Dictionary<(int Left, int Right), string[]> CrossesPatterns = new()
    {
        [(2, 1)] = ["T*****T**", "T******T*"],
        [(1, 2)] = ["T*****T**", "T******T*"],
        [(1, 0)] = ["T********"],
        [(0, 1)] = ["T********"],
    };

    /// <summary>The container holds the containee (OGC contains).</summary>
    public static bool Contains(IGeometryRelations relations, IGeometry container, IGeometry containee, CancellationToken cancellationToken) =>
        Matches(relations, container, containee, ContainsPatterns, cancellationToken);

    /// <summary>The pair shares a boundary point with disjoint interiors (OGC touches).</summary>
    public static bool Touches(IGeometryRelations relations, IGeometry left, IGeometry right, CancellationToken cancellationToken) =>
        TouchesPatterns.Any(pattern => relations.Relate(left, right, pattern, cancellationToken));

    /// <summary>Same-dimension partial overlap (OGC overlaps).</summary>
    public static bool Overlaps(IGeometryRelations relations, IGeometry left, IGeometry right, CancellationToken cancellationToken) =>
        Matches(relations, left, right, OverlapsPatterns, cancellationToken);

    /// <summary>Different-dimension crossing (OGC crosses).</summary>
    public static bool Crosses(IGeometryRelations relations, IGeometry left, IGeometry right, CancellationToken cancellationToken) =>
        Matches(relations, left, right, CrossesPatterns, cancellationToken);

    /// <summary>
    /// Whether the pair satisfies the pattern its own dimensions select —
    /// false for a dimension pair the relation does not serve, which is how a
    /// point contains nothing and same-dimension pairs never cross.
    /// </summary>
    private static bool Matches<TPattern>(
        IGeometryRelations relations,
        IGeometry left,
        IGeometry right,
        Dictionary<(int Left, int Right), TPattern> patterns,
        CancellationToken cancellationToken)
        where TPattern : IEnumerable<string>
    {
        if (!patterns.TryGetValue((SpatialDimension.Of(left), SpatialDimension.Of(right)), out var pattern))
        {
            return false;
        }

        return pattern.Any(intersectionPattern => relations.Relate(left, right, intersectionPattern, cancellationToken));
    }
}

/// <summary>
/// The topological dimension of a geometry (OGC): points 0, lines 1, areas
/// 2, anything else -1 so an unrecognised type matches no dimension-keyed
/// relation pattern.
/// </summary>
internal static class SpatialDimension
{
    private static readonly Dictionary<GeometryType, int> Dimensions = new()
    {
        [GeometryType.Point] = 0,
        [GeometryType.MultiPoint] = 0,
        [GeometryType.LineString] = 1,
        [GeometryType.MultiLineString] = 1,
        [GeometryType.Polygon] = 2,
        [GeometryType.MultiPolygon] = 2,
    };

    /// <summary>The geometry's topological dimension, or -1 when unknown.</summary>
    public static int Of(IGeometry geometry) => Dimensions.GetValueOrDefault(geometry.Type, -1);
}
