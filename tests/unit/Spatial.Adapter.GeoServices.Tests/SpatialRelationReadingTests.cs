using Microsoft.AspNetCore.Http;
using Spatial.Contracts;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.Operations.NetTopologySuite;
using Nts = NetTopologySuite.Geometries;

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
/// way fails here. Which of the two spellings each served name carries is
/// ADR-0171's: <c>spatialRel</c> names the feature's relation to the input
/// geometry, so the served <c>Within</c> is the feature-frame
/// <c>T*****FF*</c> and the served <c>Contains</c> is <c>T*F**F***</c>. The
/// reading is pinned at the edge that matters: a feature that straddles the
/// query geometry's edge contains neither it nor anything else, whether the
/// comparison is written in the feature's frame or the query's
/// (<see cref="A_feature_straddling_the_query_edge_is_not_within"/>).</para>
///
/// <para><b>Crosses.</b> The discarded implementation used the dimension-masked
/// JTS patterns <c>1********</c> / <c>0********</c>, which read a point
/// strictly inside an area as <c>Crosses</c> (the interiors meet at dimension
/// 0) and had to refuse the point/area case by hand to get that back. The
/// kept reading is the OGC pair, keyed on the pair's <em>dimension pair</em>:
/// <c>T*T******</c> (the lower-dimensional geometry's interior reaches the
/// higher-dimensional one's boundary) when the query is the higher dimension,
/// <c>T**T*****</c> when the feature is, and <c>0********</c> for two lines.
/// A dimension pair OGC does not define <c>Crosses</c> over — a point against
/// anything, or two surfaces — names no pattern, so none is asked and the
/// verdict is false. A point inside an area is therefore <c>Within</c> it and
/// not <c>Crosses</c> it, and the table gives that without a second rule to
/// keep in step with the first
/// (<see cref="A_point_pair_is_not_crosses_because_no_pattern_is_asked"/>).</para>
/// </summary>
public sealed class SpatialRelationReadingTests
{
    private static readonly NtsGeometryRelations Relations = new();

    /// <summary>
    /// The provider's own factory, so this file can read the matrix and the
    /// named predicate the engine's answers are measured against (ADR-0166)
    /// without importing the served constants it is pinning.
    /// </summary>
    private static readonly Nts.GeometryFactory Provider = new();

    private static readonly FeatureSchema Schema = new(
        [new FieldDefinition("shape", AttributeKind.Geometry, nullable: true)]);

    /// <summary>
    /// The two spellings of the containment pair: the OGC <c>within</c> mask in
    /// the query's frame and the OGC <c>contains</c> mask in the feature's
    /// frame, which are each other's transpose. Equal for every pair,
    /// because a DE-9IM matrix read in the other frame is its transpose and
    /// the pattern set is closed under transposition. This is a property of
    /// the matrix, not of the direction: it holds whichever of the two the
    /// served <c>Within</c> carries, and it held before ADR-0171 moved the
    /// served pair to the protocol's frame. What it does *not* pin is which
    /// name carries which mask — that is
    /// <see cref="SpatialRelDirectionTests"/>, which asks both operand orders
    /// of a nesting.
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
    /// The pinned <c>Within</c> reading at the query edge. <c>spatialRel</c>
    /// names the feature's relation to the input geometry, so the served
    /// <c>Within</c> reads as *the feature contains the query geometry*
    /// (ADR-0171) — the same DE-9IM mask the OGC calls <c>contains</c>, asked
    /// with the feature on the left:
    ///
    /// <list type="bullet">
    /// <item><description>a feature that straddles the query's edge has interior
    /// outside the query, so it does not <c>Within</c> it — position 3 (the
    /// feature's interior against the query's exterior) is not
    /// <c>F</c>.</description></item>
    /// <item><description>a feature surrounding the query geometry
    /// <c>Within</c> it, and a feature sharing part of the query's
    /// <em>boundary</em> is <c>Within</c> it too: position 6 (the feature's
    /// boundary against the query's exterior) is what has to be <c>F</c>, and a
    /// feature boundary lying on the query's boundary is in the query's
    /// boundary, not its exterior.</description></item>
    /// <item><description>a feature inside the query geometry is not
    /// <c>Within</c> it — that is the served <c>Contains</c> — and a feature
    /// whose interior misses the query's interior is not <c>Within</c>
    /// it.</description></item>
    /// </list>
    /// </summary>
    [Theory]
    [InlineData("small-in-big", false)]
    [InlineData("corner-in-big", false)]
    [InlineData("point-in-area", false)]
    [InlineData("equal-areas", true)]
    [InlineData("straddle", false)]
    [InlineData("big-around-small", true)]
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
    /// <item><description>a point inside an area query is <c>Contained</c> in
    /// it, not <c>Crosses</c> it — a point has no extent to cross, and its own
    /// boundary is empty, so neither crosses pattern can meet.</description></item>
    /// <item><description>a point on an area's boundary <c>Touches</c> it,
    /// and does not <c>Crosses</c> it: the interiors are disjoint.</description></item>
    /// <item><description>a line strictly inside an area query is
    /// <c>Contained</c> in it, not <c>Crosses</c> it: the line's interior
    /// never reaches the area's boundary.</description></item>
    /// <item><description>a line that runs out of the area both ways
    /// <c>Crosses</c> it, and so does the area.</description></item>
    /// <item><description>a line lying along an area's edge <c>Touches</c> it
    /// and does not <c>Crosses</c> it: the interiors are disjoint.</description></item>
    /// <item><description>equal-dimensional and point/polygon pairs are never
    /// <c>Crosses</c>.</description></item>
    /// <item><description>two crossing lines <c>Crosses</c>, two collinear
    /// lines sharing a span <c>Overlap</c>, and a span lying wholly inside the
    /// other line is neither — the line/line containment
    /// (<see cref="A_line_pair_is_read_by_the_dimension_its_interiors_meet_in"/>).</description></item>
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
    [InlineData("lines-crossing", true)]
    [InlineData("lines-sharing-a-span", false)]
    [InlineData("line-within-span", false)]
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
    /// <c>Contains</c> true, which is what a point inside an input geometry is:
    /// the served <c>Contains</c> names the input geometry's relation to the
    /// feature (ADR-0171).
    /// </summary>
    [Fact]
    public async Task A_point_inside_an_area_query_is_contained_by_it_and_not_crosses()
    {
        Assert.False(await MatchesAsync("point-in-area", EsriFeatureQuery.Crosses));
        Assert.True(await MatchesAsync("point-in-area", EsriFeatureQuery.Contains));
        Assert.False(await MatchesAsync("point-in-area", EsriFeatureQuery.Within));
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
    /// The point/area case is not in this table at all: OGC defines
    /// <c>Crosses</c> over a mixed-dimension pair of a surface and a curve,
    /// so the dimension pair (0, 2) names no pattern and none is asked
    /// (<see cref="A_point_pair_is_not_crosses_because_no_pattern_is_asked"/>).
    /// </summary>
    [Theory]
    [InlineData("line-inside-area", "T*T******", false)]
    [InlineData("line-crossing-area", "T*T******", true)]
    [InlineData("area-around-line", "T**T*****", false)]
    [InlineData("area-across-line", "T**T*****", true)]
    public async Task The_pattern_asked_is_the_one_for_the_lower_dimensional_side(
        string pair,
        string expected,
        bool expectedMatch)
    {
        var asked = new RecordingRelations(Relations);

        Assert.Equal(expectedMatch, await MatchedAsync(pair, EsriFeatureQuery.Crosses, asked));
        Assert.Equal([expected], asked.Patterns);
    }

    /// <summary>
    /// The point/area case, and every other dimension pair OGC defines no
    /// <c>Crosses</c> over, stated as the fact it is on the served path: the
    /// dimension pair names no pattern, so the match asks <b>no pattern at
    /// all</b> and answers false.
    ///
    /// <para>This is the divergence from the discarded reading, and it is the
    /// half of it that the table alone cannot give. The discarded
    /// implementation keyed <c>Crosses</c> on dimension masks
    /// (<c>0********</c> / <c>1********</c>) rather than on the dimension
    /// pair, so it did ask a pattern for a point against an area — and
    /// <c>0********</c> is true of a point strictly inside a polygon, which is
    /// why that reading had to carry a hand-written dimension switch to take
    /// the answer back. Asking nothing is the same verdict without the second
    /// rule to keep in step with the first.</para>
    /// </summary>
    [Theory]
    [InlineData("point-in-area")]
    [InlineData("area-around-point")]
    [InlineData("point-in-point")]
    [InlineData("point-on-area-edge")]
    [InlineData("equal-areas")]
    public async Task A_point_pair_is_not_crosses_because_no_pattern_is_asked(string pair)
    {
        var asked = new RecordingRelations(Relations);

        Assert.False(await MatchedAsync(pair, EsriFeatureQuery.Crosses, asked));
        Assert.Empty(asked.Patterns);
    }

    /// <summary>
    /// The line/line rows, which the served table gained after the discarded
    /// reading was written and which pins them here rather than leaving them
    /// to the ADR. <c>Crosses</c> asks <c>0********</c> over a line/line pair
    /// and <c>Overlaps</c> asks <c>1*T***T**</c>, so the pair is settled by
    /// the dimension the interiors meet in, and by whether the exteriors meet
    /// as well: a crossing pair meets in dimension zero (<c>Crosses</c>), a
    /// pair sharing a span meets in dimension one with the feature's exterior
    /// reaching the query's interior (<c>Overlaps</c>), and a pair whose span
    /// lies wholly inside the feature's is neither — it is the line/line
    /// containment, because <c>1*T***T**</c> also asks position 7 (the
    /// feature's exterior against the query's interior) to meet: a pair only
    /// overlaps while *each* line reaches past the other
    /// (SpatialEngine-u2x.56).
    ///
    /// <para>The discarded reading reached the first two verdicts only by
    /// dimension masks with no containment case at all, so the third row is
    /// the one that has no counterpart to compare against.</para>
    /// </summary>
    [Theory]
    [InlineData("lines-crossing", true, false)]
    [InlineData("lines-sharing-a-span", false, true)]
    [InlineData("line-within-span", false, false)]
    public async Task A_line_pair_is_read_by_the_dimension_its_interiors_meet_in(
        string pair,
        bool expectedCrosses,
        bool expectedOverlaps)
    {
        var crosses = new RecordingRelations(Relations);
        Assert.Equal(expectedCrosses, await MatchedAsync(pair, EsriFeatureQuery.Crosses, crosses));
        Assert.Equal(["0********"], crosses.Patterns);

        var overlaps = new RecordingRelations(Relations);
        Assert.Equal(expectedOverlaps, await MatchedAsync(pair, EsriFeatureQuery.Overlaps, overlaps));
        Assert.Equal(["1*T***T**"], overlaps.Patterns);
    }

    /// <summary>
    /// The one pair where the served table and the provider's own
    /// <c>Crosses</c> disagree, pinned as a decision rather than left to the
    /// characterisation battery: a line lying wholly inside the closed area
    /// and touching its boundary from inside. The served alternation asks the
    /// interiors to meet and the line's interior to reach the area's boundary
    /// (positions 1 and 4) and says nothing about the line leaving the area,
    /// so with the area on the left it answers true; the provider's predicate
    /// also requires position 7 — the area's exterior against the line's
    /// interior — and answers false. This engine serves the alternation
    /// (ADR-0106) and the divergence stays documented (ADR-0166, ADR-0169).
    ///
    /// <para>The pin also states the half of it that is easy to miss: the
    /// alternation's answer depends on the frame. With the line on the left
    /// the mask is <c>T*T******</c>, whose position 3 (the line's exterior
    /// against the area's interior) is <c>F</c> for this pair, so the served
    /// answer is false there — the same pair, one answer each way. That is a
    /// consequence of ADR-0106's frame (the feature on the left — the
    /// direction ADR-0171 names: <c>spatialRel</c> is the feature's relation
    /// to the input geometry) rather than a second rule, and it is the
    /// strongest argument for the reading this record keeps, so it is stated
    /// where a client can find it.</para>
    /// </summary>
    [Fact]
    public async Task A_line_inside_an_area_that_touches_its_boundary_is_served_the_ogc_alternation()
    {
        // The area on the left: the served mask is T**T***** and it answers true.
        var areaFirst = new RecordingRelations(Relations);
        Assert.True(await MatchedAsync("area-touch-line-inside", EsriFeatureQuery.Crosses, areaFirst));
        Assert.Equal(["T**T*****"], areaFirst.Patterns);

        // The line on the left: the mask is T*T*****, whose position 3 is F
        // for this pair, so the same pair answers false.
        var lineFirst = new RecordingRelations(Relations);
        Assert.False(await MatchedAsync("line-touch-area-boundary-inside", EsriFeatureQuery.Crosses, lineFirst));
        Assert.Equal(["T*T******"], lineFirst.Patterns);

        // The matrix behind both, rendered by the engine's own provider:
        // position 4 non-empty and position 7 empty in the area's frame, and
        // the transpose that says the same thing with the line on the left.
        var providerArea = Provider.CreatePolygon(
        [
            new Nts.Coordinate(0, 0),
            new Nts.Coordinate(10, 0),
            new Nts.Coordinate(10, 10),
            new Nts.Coordinate(0, 10),
            new Nts.Coordinate(0, 0),
        ]);
        var providerLine = Provider.CreateLineString(
        [
            new Nts.Coordinate(5, 2),
            new Nts.Coordinate(10, 5),
            new Nts.Coordinate(5, 8),
        ]);
        Assert.Equal("1020F1FF2", providerArea.Relate(providerLine).ToString());
        Assert.Equal("10F0FF212", providerLine.Relate(providerArea).ToString());

        // The provider's own predicate answers false in both frames, which is
        // the divergence ADR-0166 names and this record keeps.
        Assert.False(providerArea.Crosses(providerLine));
        Assert.False(providerLine.Crosses(providerArea));
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

    private static async Task<bool> MatchesAsync(string pair, string spatialRel) =>
        await MatchedAsync(pair, spatialRel, Relations);

    /// <summary>
    /// The one call every case here makes: run the served match path for a
    /// pair under a named <c>spatialRel</c>, asking <paramref name="asked"/>
    /// for the relation, so the patterns the path actually consulted can be
    /// asserted and not merely the verdict.
    /// </summary>
    private static async Task<bool> MatchedAsync(string pair, string spatialRel, IGeometryRelations asked)
    {
        var (feature, query) = Pair(pair);
        return FeatureSpatialMatcher.Matches(
            new FeatureSpatialMatcher.MatchCandidate(
                await QueryAsync(query, spatialRel), Feature(feature), 1, query, asked),
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
        "area-touch-line-inside" => (Area(), TouchingLine()),
        "line-touch-area-boundary-inside" => (TouchingLine(), Area()),
        "area-across-line" => (Square(5, 0, 15, 10), Line(-5, 5, 15, 5)),
        "area-around-point" => (Area(), Point(5, 5)),
        "lines-crossing" => (Line(-5, 5, 15, 5), Line(5, -5, 5, 15)),
        "lines-sharing-a-span" => (Line(-5, 0, 15, 0), Line(0, 0, 20, 0)),
        "line-within-span" => (Line(-5, 0, 15, 0), Line(0, 0, 5, 0)),
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

    /// <summary>
    /// The line the divergence class is made of: it lies wholly inside the
    /// closed unit square and touches the boundary at its own interior vertex
    /// (10,5), so the line's interior reaches the area's boundary and never
    /// its exterior. Matrix <c>1020F1FF2</c> with the area on the left.
    /// </summary>
    private static LineString TouchingLine() =>
        GeometryFactory.CreateLineString([new Coordinate(5, 2), new Coordinate(10, 5), new Coordinate(5, 8)]);
}
