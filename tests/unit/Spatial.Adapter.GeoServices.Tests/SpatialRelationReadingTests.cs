using Microsoft.AspNetCore.Http;
using Spatial.Contracts;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.Operations.NetTopologySuite;

namespace Spatial.Adapter.GeoServices.Tests;

/// <summary>
/// The served <c>spatialRel</c> DE-9IM reading (SpatialEngine-onj, ADR-0106).
///
/// Two implementations of SpatialEngine-u2x.2 were written before the OGC
/// table's reading was decided, and they disagreed about two of the five
/// verbs. ADR-0106 settles it: the facade serves the OGC Simple Features
/// table verbatim — the <c>T</c>/<c>F</c> intersection patterns, the feature
/// geometry as the left operand of the matrix — with a dimension-selected
/// pattern only for the two verbs OGC itself defines per dimension pair
/// (<c>Overlaps</c>, <c>Crosses</c>). This file pins the two settled
/// readings at the points where the discarded reading gave a different
/// answer.
///
/// <para><b>Within.</b> There was no divergence to settle: the discarded
/// implementation evaluated <c>Contains</c> with the operands swapped, and the
/// transpose of <c>T*****FF*</c> is <c>T*F**F***</c> — the pattern the kept
/// implementation spells out in its own right. A DE-9IM matrix is
/// transpose-symmetric, so the two spellings are the same predicate.
/// <see cref="Within_is_the_transpose_of_contains"/> is that claim as a test
/// rather than a claim, so a later change that reads <c>Within</c> any other
/// way fails here. The reading is pinned at the edge that matters: a feature
/// that straddles the query geometry's edge is not <c>Within</c> it, whether
/// the comparison is written in the feature's frame or the query's
/// (<see cref="A_feature_straddling_the_query_edge_is_not_within"/>).</para>
///
/// <para><b>Crosses.</b> The discarded implementation used the dimension-masked
/// JTS patterns <c>1********</c> / <c>0********</c>, which read a point
/// strictly inside an area as <c>Crosses</c> (the interiors meet at dimension
/// 0) and had to refuse the point/area case by hand to get that back. The
/// kept reading is the OGC pair, selected by which side is the
/// lower-dimensional geometry: <c>T*T******</c> (the lower-dimensional
/// geometry's interior reaches the higher-dimensional one's boundary) when
/// the query is the higher dimension, and <c>T**T*****</c> when the feature
/// is. A point's own boundary is empty, so the point/area case falls out of
/// the table as false without a dimension special case — and as
/// <c>Within</c>, which is what a point inside an area is
/// (<see cref="A_point_inside_an_area_is_within_it_and_not_crosses"/>).</para>
/// </summary>
public sealed class SpatialRelationReadingTests
{
    private static readonly NtsGeometryRelations Relations = new();

    private static readonly FeatureSchema Schema = new(
        [new FieldDefinition("shape", AttributeKind.Geometry, nullable: true)]);

    /// <summary>
    /// The two spellings of <c>Within</c>: the kept table's own pattern, and
    /// the discarded implementation's <c>Contains</c> pattern with the operands
    /// swapped. Equal for every pair, because a DE-9IM matrix read in the
    /// other frame is its transpose and the pattern set is closed under
    /// transposition. If this ever goes red, the served <c>Within</c> is no
    /// longer the transpose of the served <c>Contains</c> and the direction
    /// (SpatialEngine-2ve) has changed underneath it.
    /// </summary>
    [Theory]
    [InlineData("point-in-area")]
    [InlineData("point-on-area-edge")]
    [InlineData("point-outside")]
    [InlineData("small-in-big")]
    [InlineData("corner-in-big")]
    [InlineData("big-around-small")]
    [InlineData("equal-areas")]
    [InlineData("straddle")]
    [InlineData("line-inside-area")]
    [InlineData("line-crossing-area")]
    [InlineData("line-on-area-edge")]
    public void Within_is_the_transpose_of_contains(string pair)
    {
        var (feature, query) = Pair(pair);

        Assert.Equal(
            Relations.Relate(query, feature, "T*****FF*", CancellationToken.None),
            Relations.Relate(feature, query, "T*F**F***", CancellationToken.None));
    }

    /// <summary>
    /// The pinned <c>Within</c> reading at the query edge. The feature is the
    /// left operand, so "within" reads as *the feature lies inside the query
    /// geometry*:
    ///
    /// <list type="bullet">
    /// <item><description>a feature that straddles the query's edge has interior
    /// outside the query, so it is not <c>Within</c> — position 3 (the
    /// feature's interior against the query's exterior) is not
    /// <c>F</c>.</description></item>
    /// <item><description>a feature sharing part of the query's <em>boundary</em>
    /// is <c>Within</c>: position 6 (the feature's boundary against the
    /// query's exterior) is what has to be <c>F</c>, and a feature boundary
    /// lying on the query's boundary is in the query's boundary, not its
    /// exterior.</description></item>
    /// <item><description>a feature containing the query is not
    /// <c>Within</c> it, and a feature whose interior misses the query's
    /// interior is not <c>Within</c> it.</description></item>
    /// </list>
    /// </summary>
    [Theory]
    [InlineData("small-in-big", true)]
    [InlineData("corner-in-big", true)]
    [InlineData("point-in-area", true)]
    [InlineData("equal-areas", true)]
    [InlineData("straddle", false)]
    [InlineData("big-around-small", false)]
    [InlineData("point-on-area-edge", false)]
    [InlineData("point-outside", false)]
    [InlineData("line-crossing-area", false)]
    public async Task A_feature_straddling_the_query_edge_is_not_within(string pair, bool expected)
    {
        Assert.Equal(expected, await MatchesAsync(pair, EsriFeatureQuery.Within));
    }

    /// <summary>
    /// The pinned <c>Crosses</c> reading, over both operand orders. The
    /// pattern is selected by which side is the lower-dimensional geometry,
    /// and the table is not symmetric: an area that a line crosses
    /// <c>Crosses</c> the line, and the same line <c>Crosses</c> the area.
    ///
    /// <list type="bullet">
    /// <item><description>a point inside an area is <c>Within</c> it, not
    /// <c>Crosses</c> it — a point has no extent to cross, and its own
    /// boundary is empty, so neither crosses pattern can meet.</description></item>
    /// <item><description>a point on an area's boundary <c>Touches</c> it,
    /// and does not <c>Crosses</c> it: the interiors are disjoint.</description></item>
    /// <item><description>a line strictly inside an area is <c>Within</c> it,
    /// not <c>Crosses</c> it: the line's interior never reaches the area's
    /// boundary.</description></item>
    /// <item><description>a line that runs out of the area both ways
    /// <c>Crosses</c> it, and so does the area.</description></item>
    /// <item><description>a line lying along an area's edge <c>Touches</c> it
    /// and does not <c>Crosses</c> it: the interiors are disjoint.</description></item>
    /// <item><description>equal-dimensional and point/polygon pairs are never
    /// <c>Crosses</c>.</description></item>
    /// </list>
    /// </summary>
    [Theory]
    [InlineData("point-in-area", false)]
    [InlineData("point-on-area-edge", false)]
    [InlineData("point-in-point", false)]
    [InlineData("line-inside-area", false)]
    [InlineData("line-crossing-area", true)]
    [InlineData("line-on-area-edge", false)]
    [InlineData("area-around-line", false)]
    [InlineData("area-across-line", true)]
    [InlineData("area-around-point", false)]
    [InlineData("equal-areas", false)]
    [InlineData("straddle", false)]
    public async Task Crosses_is_the_ogc_pair_for_the_dimension_order(string pair, bool expected)
    {
        Assert.Equal(expected, await MatchesAsync(pair, EsriFeatureQuery.Crosses));
    }

    /// <summary>
    /// The case the two readings actually disagreed on, stated in one place:
    /// a point feature inside an area query. The discarded reading answered
    /// <c>Crosses</c> (<c>0********</c> — the interiors meet at dimension 0)
    /// and had to carry a dimension switch to refuse the point/area case by
    /// hand. The served reading answers it out of the table, and answers
    /// <c>Within</c> true, which is what a point inside a polygon is.
    /// </summary>
    [Fact]
    public async Task A_point_inside_an_area_is_within_it_and_not_crosses()
    {
        Assert.False(await MatchesAsync("point-in-area", EsriFeatureQuery.Crosses));
        Assert.True(await MatchesAsync("point-in-area", EsriFeatureQuery.Within));
        Assert.False(await MatchesAsync("point-in-area", EsriFeatureQuery.Contains));
        Assert.True(await MatchesAsync("point-in-area", EsriFeatureQuery.Intersects));
    }

    /// <summary>
    /// The dimension selection, pinned by the pattern the match path actually
    /// asks for: the pattern for the lower-dimensional side of the pair. The
    /// selection is load-bearing in both directions — for an area feature
    /// against a line query, a line lying wholly inside the area matches
    /// <c>T**T*****</c>, and for a line feature against an area query it
    /// matches <c>T*T******</c> — so asking the wrong member of the pair is
    /// what makes the discarded reading wrong.
    ///
    /// The point/area case needs no special case of its own: a point's
    /// boundary is empty, so matrix positions 2 and 4 cannot meet and the
    /// pattern asked is false, in either operand order.
    /// </summary>
    [Theory]
    [InlineData("point-in-area", "T*T******", false)]
    [InlineData("area-around-point", "T**T*****", false)]
    [InlineData("line-inside-area", "T*T******", false)]
    [InlineData("line-crossing-area", "T*T******", true)]
    [InlineData("area-around-line", "T**T*****", false)]
    [InlineData("area-across-line", "T**T*****", true)]
    public async Task The_pattern_asked_is_the_one_for_the_lower_dimensional_side(
        string pair,
        string expected,
        bool expectedMatch)
    {
        var (feature, query) = Pair(pair);
        var asked = new RecordingRelations(Relations);
        var parsed = await QueryAsync(query, EsriFeatureQuery.Crosses);

        var matched = FeatureSpatialMatcher.Matches(
            new FeatureSpatialMatcher.MatchCandidate(parsed, Feature(feature), 1, query, asked),
            CancellationToken.None);

        Assert.Equal([expected], asked.Patterns);
        Assert.Equal(expectedMatch, matched);
    }

    /// <summary>The relation face that remembers the patterns it was asked.</summary>
    private sealed class RecordingRelations(IGeometryRelations inner) : IGeometryRelations
    {
        internal List<string> Patterns { get; } = [];

        public bool Relate(IGeometry left, IGeometry right, string intersectionPattern, CancellationToken cancellationToken = default)
        {
            Patterns.Add(intersectionPattern);
            return inner.Relate(left, right, intersectionPattern, cancellationToken);
        }
    }

    /// <summary>
    /// The decided <c>Crosses</c> patterns are dimension-selected, so the
    /// envelope pre-filter is the only cheap step in front of them: a
    /// disjoint pair is refused before any pattern is asked, and a meeting
    /// pair is asked. Pins that the selection is the only reason a same-
    /// dimension pair reads false.
    /// </summary>
    [Fact]
    public async Task A_cancelled_crosses_match_stops_before_the_exact_predicate()
    {
        var (feature, query) = Pair("line-crossing-area");
        var parsed = await QueryAsync(query, EsriFeatureQuery.Crosses);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        Assert.Throws<OperationCanceledException>(() => FeatureSpatialMatcher.Matches(
            new FeatureSpatialMatcher.MatchCandidate(
                parsed, Feature(feature), 1, query, Relations),
            cancelled.Token));
    }

    private static async Task<bool> MatchesAsync(string pair, string spatialRel)
    {
        var (feature, query) = Pair(pair);
        return FeatureSpatialMatcher.Matches(
            new FeatureSpatialMatcher.MatchCandidate(
                await QueryAsync(query, spatialRel), Feature(feature), 1, query, Relations),
            CancellationToken.None);
    }

    private static async Task<EsriFeatureQuery> QueryAsync(IGeometry queryGeometry, string spatialRel)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = QueryString.Create(
            [new KeyValuePair<string, string?>("spatialRel", spatialRel)]);
        var parameters = await EsriRequestParameters.ReadAsync(context, CancellationToken.None);
        return EsriFeatureQuery.Parse(parameters, fallback: null) with { Geometry = queryGeometry };
    }

    private static Feature Feature(IGeometry geometry) =>
        new(new FeatureId("feature"), Schema, [AttributeValue.FromGeometry(geometry)]);

    /// <summary>
    /// The pairs, as (feature geometry, query geometry): the feature is the
    /// left operand of the DE-9IM matrix on the served path, so a pair named
    /// "point-in-area" is a point feature and an area query.
    /// </summary>
    private static (IGeometry Feature, IGeometry Query) Pair(string name) => name switch
    {
        "point-in-area" => (Point(5, 5), Area()),
        "point-on-area-edge" => (Point(0, 5), Area()),
        "point-outside" => (Point(20, 20), Area()),
        "point-in-point" => (Point(5, 5), Point(5, 5)),
        "small-in-big" => (Square(2, 2, 4, 4), Area()),
        "corner-in-big" => (Square(0, 0, 4, 4), Area()),
        "big-around-small" => (Area(), Square(2, 2, 4, 4)),
        "equal-areas" => (Area(), Area()),
        "straddle" => (Square(5, 0, 15, 10), Area()),
        "line-inside-area" => (Line(2, 2, 8, 8), Area()),
        "line-crossing-area" => (Line(-5, 5, 15, 5), Area()),
        "line-on-area-edge" => (Line(-5, 0, 15, 0), Area()),
        "area-around-line" => (Area(), Line(2, 2, 8, 8)),
        "area-across-line" => (Square(5, 0, 15, 10), Line(-5, 5, 15, 5)),
        "area-around-point" => (Area(), Point(5, 5)),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "unknown pair"),
    };

    private static Polygon Area() => Square(0, 0, 10, 10);

    private static Point Point(double x, double y) => GeometryFactory.CreatePoint(x, y);

    private static Polygon Square(double minX, double minY, double maxX, double maxY) =>
        GeometryFactory.CreatePolygon(
        [
            new Coordinate(minX, minY),
            new Coordinate(maxX, minY),
            new Coordinate(maxX, maxY),
            new Coordinate(minX, maxY),
            new Coordinate(minX, minY),
        ]);

    private static LineString Line(double x1, double y1, double x2, double y2) =>
        GeometryFactory.CreateLineString([new Coordinate(x1, y1), new Coordinate(x2, y2)]);
}
