using Microsoft.AspNetCore.Http;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.Esri.Codec;
using Spatial.Operations.NetTopologySuite;

namespace Spatial.Adapter.GeoServices.Tests;

/// <summary>
/// The served <c>spatialRel</c> relations (spec §9.1.4) against the OGC DE-9IM
/// matrices, hand-computed for polygon, line and point feature/query
/// geometries — each scenario names the matrix it was computed from. The
/// envelope test stays the cheap pre-filter; the exact predicate decides the
/// candidates. Red-first: the envelope approximation read a query filling the
/// feature's hole as contained, read a hole against its outer square as
/// contained, and read collinear or crossing lines as touching.
/// </summary>
public sealed class SpatialRelExactnessTests
{
    private static readonly NtsGeometryOperations Operations = new();

    private static readonly NtsGeometryRelations Relations = new();

    private static readonly FeatureSchema Schema = new([new FieldDefinition("geometry", AttributeKind.Geometry)]);

    /// <summary>Every served relation and the hand-computed answer for one geometry pair.</summary>
    public static TheoryData<string, IGeometry, IGeometry, RelationMatrix> De9ImMatrix() => new()
    {
        // Strictly nested squares: the query's points are in the feature's
        // interior or on its boundary, and the interiors meet.
        { "square contains inner square 212FF1FF2", Square(0, 0, 10, 10), Square(1, 1, 2, 2), new RelationMatrix { Envelope = true, Intersects = true, Contains = true } },
        // A nested square sharing an edge is still contained: the query's
        // exterior is not in the feature's exterior.
        { "square contains edge-sharing inner square 212F11FF2", Square(0, 0, 10, 10), Square(0, 0, 5, 5), new RelationMatrix { Envelope = true, Intersects = true, Contains = true } },
        // Identical squares: contained both ways, nothing else.
        { "identical squares 2FFF1FFF2", Square(0, 0, 10, 10), Square(0, 0, 10, 10), new RelationMatrix { Envelope = true, Intersects = true, Contains = true, Within = true } },
        // Partial corner overlap: interiors meet, each has points in the
        // other's exterior, so neither contains.
        { "overlapping squares 212101212", Square(0, 0, 10, 10), Square(5, 5, 15, 15), new RelationMatrix { Envelope = true, Intersects = true, Overlaps = true } },
        // Shared edge: interiors disjoint, a boundary segment in common.
        { "edge-sharing squares FF2F11212", Square(0, 0, 10, 10), Square(10, 0, 20, 10), new RelationMatrix { Envelope = true, Intersects = true, Touches = true } },
        // The query fills the feature's hole: the intersection is the query's
        // own boundary, so the feature's interior meets the query's exterior
        // and the query is not contained — the pair touches.
        { "query fills the feature hole FF2F112F2", Donut(), Square(4, 4, 6, 6), new RelationMatrix { Envelope = true, Intersects = true, Touches = true } },
        // The query is the feature's outer square: the query's boundary lies
        // in the feature's exterior, so neither contains the other and the
        // feature is not within the query either.
        { "query is the feature outer square 2FF11F2F2", Donut(), Square(0, 0, 10, 10), new RelationMatrix { Envelope = true, Intersects = true, Within = true } },
        // A point on a line: contained by the line, and a different-dimension
        // interior intersection. A point at the line's end is on the line's
        // boundary, so the pair touches instead.
        { "point in a line interior 0F1FF0FF2", Line((0, 0), (10, 0)), Point(5, 0), new RelationMatrix { Envelope = true, Intersects = true, Contains = true, Crosses = true } },
        { "point at a line end FF10F0FF2", Line((0, 0), (10, 0)), Point(10, 0), new RelationMatrix { Envelope = true, Intersects = true, Touches = true } },
        // A chord with both ends on the boundary is contained by the polygon,
        // not crossing: a line inside the polygon is contained too, and only
        // a line reaching past the polygon crosses it.
        { "line inside a polygon 102FF1FF2", Square(0, 0, 10, 10), Line((1, 1), (2, 2)), new RelationMatrix { Envelope = true, Intersects = true, Contains = true } },
        { "line half out of a polygon 1020F1102", Square(0, 0, 10, 10), Line((5, 5), (15, 5)), new RelationMatrix { Envelope = true, Intersects = true, Crosses = true } },
        { "line through a polygon 1F20F1102", Square(0, 0, 10, 10), Line((-5, 5), (15, 5)), new RelationMatrix { Envelope = true, Intersects = true, Crosses = true } },
        // A point inside a square is within it; the square contains it. A
        // point never crosses an area, so the pair carries no crossing.
        { "point within a square 0FFFFF212", Point(5, 5), Square(0, 0, 10, 10), new RelationMatrix { Envelope = true, Intersects = true, Within = true } },
        { "square contains a point 0F2FF1FF2", Square(0, 0, 10, 10), Point(5, 5), new RelationMatrix { Envelope = true, Intersects = true, Contains = true } },
        // Collinear lines sharing a stretch overlap; crossing lines meet in a
        // point, which is no served relation.
        { "collinear overlapping lines 1010F0102", Line((0, 0), (10, 0)), Line((5, 0), (15, 0)), new RelationMatrix { Envelope = true, Intersects = true, Overlaps = true } },
        { "crossing lines 0F1FF0102", Line((0, 0), (10, 10)), Line((0, 10), (10, 0)), new RelationMatrix { Envelope = true, Intersects = true } },
        // Disjoint envelopes: the pre-filter answers every relation false.
        { "disjoint squares FF2FF1212", Square(0, 0, 1, 1), Square(2, 2, 3, 3), new RelationMatrix() },
        { "disjoint points FF0FFF0F2", Point(0, 0), Point(1, 1), new RelationMatrix() },
    };

    [Theory]
    [MemberData(nameof(De9ImMatrix))]
    public async Task Every_served_relation_matches_the_hand_computed_matrix(
        string scenario, IGeometry featureGeometry, IGeometry queryGeometry, RelationMatrix expected)
    {
        foreach (var (spatialRel, answer) in expected.Cases())
        {
            Assert.True(
                answer == await MatchAsync(spatialRel, featureGeometry, queryGeometry),
                $"{spatialRel} should be {(answer ? "true" : "false")} for {scenario}.");
        }
    }

    [Fact]
    public async Task Contains_rejects_a_query_that_fills_the_feature_hole()
    {
        // The regression: the envelope approximation contained any query whose
        // intersection envelope equalled its own envelope, and a hole's
        // boundary has exactly that envelope.
        Assert.False(await MatchAsync(EsriFeatureQuery.Contains, Donut(), Square(4, 4, 6, 6)));
    }

    [Fact]
    public async Task Contains_rejects_a_query_that_wraps_the_feature_hole()
    {
        // The other direction of the same approximation: the hole leaves the
        // query's boundary in the feature's exterior, so no containment.
        Assert.False(await MatchAsync(EsriFeatureQuery.Contains, Donut(), Square(0, 0, 10, 10)));
    }

    [Fact]
    public async Task Touches_accepts_a_query_that_fills_the_feature_hole() =>
        Assert.True(await MatchAsync(EsriFeatureQuery.Touches, Donut(), Square(4, 4, 6, 6)));

    [Fact]
    public async Task Touches_rejects_collinear_lines_that_overlap()
    {
        // The intersection is a stretch of the line, not a boundary: the
        // envelope approximation read its degenerate envelope as a touch.
        Assert.False(await MatchAsync(EsriFeatureQuery.Touches, Line((0, 0), (10, 0)), Line((5, 0), (15, 0))));
    }

    [Fact]
    public async Task Touches_rejects_lines_that_cross() =>
        Assert.False(await MatchAsync(EsriFeatureQuery.Touches, Line((0, 0), (10, 10)), Line((0, 10), (10, 0))));

    [Fact]
    public async Task Within_rejects_a_feature_filling_the_query_hole() =>
        Assert.False(await MatchAsync(EsriFeatureQuery.Within, Square(4, 4, 6, 6), Donut()));

    [Fact]
    public async Task Overlaps_rejects_a_line_fully_inside_a_polygon() =>
        Assert.False(await MatchAsync(EsriFeatureQuery.Overlaps, Square(0, 0, 10, 10), Line((1, 1), (2, 2))));

    [Fact]
    public async Task Overlaps_rejects_a_different_dimension_pair() =>
        Assert.False(await MatchAsync(EsriFeatureQuery.Overlaps, Point(1, 1), Square(0, 0, 2, 2)));

    [Fact]
    public async Task An_unknown_spatial_rel_is_rejected_by_name() =>
        await Assert.ThrowsAsync<EsriInteropException>(
            () => MatchAsync("esriSpatialRelBogus", Square(0, 0, 10, 10), Square(1, 1, 2, 2)));

    [Fact]
    public async Task A_cancelled_match_stops_on_the_exact_predicate()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => MatchAsync(EsriFeatureQuery.Contains, Square(0, 0, 10, 10), Square(1, 1, 2, 2), cancelled.Token));
    }

    private static async Task<bool> MatchAsync(
        string spatialRel, IGeometry featureGeometry, IGeometry queryGeometry, CancellationToken cancellationToken = default)
    {
        var query = await QueryAsync(spatialRel);
        return FeatureSpatialMatcher.Matches(
            new FeatureSpatialMatcher.MatchCandidate(query, Feature(featureGeometry), 1, queryGeometry, Operations, Relations),
            cancellationToken);
    }

    private static Feature Feature(IGeometry geometry) => new(new FeatureId("1"), Schema, [AttributeValue.FromGeometry(geometry)]);

    private static async Task<EsriFeatureQuery> QueryAsync(string spatialRel)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = QueryString.Create([new KeyValuePair<string, string?>("spatialRel", spatialRel)]);
        return EsriFeatureQuery.Parse(await EsriRequestParameters.ReadAsync(context, CancellationToken.None), fallback: null);
    }

    private static Point Point(double x, double y) => GeometryFactory.CreatePoint(x, y);

    private static LineString Line(params (double X, double Y)[] vertices) =>
        GeometryFactory.CreateLineString([.. vertices.Select(vertex => new Coordinate(vertex.X, vertex.Y))]);

    private static Polygon Square(double minX, double minY, double maxX, double maxY) =>
        GeometryFactory.CreatePolygon(
        [
            new Coordinate(minX, minY),
            new Coordinate(maxX, minY),
            new Coordinate(maxX, maxY),
            new Coordinate(minX, maxY),
            new Coordinate(minX, minY),
        ]);

    /// <summary>A square with a square hole: the feature whose hole a query may fill.</summary>
    private static Polygon Donut() => GeometryFactory.CreatePolygon(
        GeometryFactory.CreateLineString(
        [
            new Coordinate(0, 0),
            new Coordinate(10, 0),
            new Coordinate(10, 10),
            new Coordinate(0, 10),
            new Coordinate(0, 0),
        ]),
        [GeometryFactory.CreateLineString(
        [
            new Coordinate(4, 4),
            new Coordinate(4, 6),
            new Coordinate(6, 6),
            new Coordinate(6, 4),
            new Coordinate(4, 4),
        ])]);

    /// <summary>
    /// The hand-computed DE-9IM answer for one geometry pair: every served
    /// relation, defaulting to false so each scenario states only what it
    /// demonstrates.
    /// </summary>
    public sealed record RelationMatrix
    {
        public bool Envelope { get; init; }

        public bool Intersects { get; init; }

        public bool Contains { get; init; }

        public bool Within { get; init; }

        public bool Touches { get; init; }

        public bool Overlaps { get; init; }

        public bool Crosses { get; init; }

        public IEnumerable<(string SpatialRel, bool Answer)> Cases()
        {
            yield return (EsriFeatureQuery.EnvelopeIntersects, Envelope);
            yield return (EsriFeatureQuery.Intersects, Intersects);
            yield return (EsriFeatureQuery.Contains, Contains);
            yield return (EsriFeatureQuery.Within, Within);
            yield return (EsriFeatureQuery.Touches, Touches);
            yield return (EsriFeatureQuery.Overlaps, Overlaps);
            yield return (EsriFeatureQuery.Crosses, Crosses);
        }
    }
}
