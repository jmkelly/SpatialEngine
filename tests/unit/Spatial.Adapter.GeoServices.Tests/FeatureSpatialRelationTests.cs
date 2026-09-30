using Microsoft.AspNetCore.Http;
using Spatial.Contracts;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.Esri.Codec;
using Spatial.Operations.NetTopologySuite;
using Spatial.Transformations.ProjNet;

namespace Spatial.Adapter.GeoServices.Tests;

/// <summary>
/// The served <c>spatialRel</c> verbs, served exactly (SpatialEngine-u2x.2):
/// every verb is its DE-9IM pattern over <c>IGeometryRelations.Relate</c>, so
/// the cases the envelope approximation got wrong are the cases pinned here.
///
/// The fixture is a unit square feature and eleven query geometries whose
/// DE-9IM matrices against it are hand-computed. The matrix rows are the
/// feature's components and the columns the query's, in
/// interior/boundary/exterior order, so position 1 is interior∩interior,
/// position 2 feature-boundary∩query-interior, position 4
/// feature-interior∩query-boundary, position 5 boundary∩boundary and
/// position 9 exterior∩exterior; <c>T</c> below is "non-empty", and
/// <c>F</c> is "disjoint".
///
/// | Query geometry | DE-9IM | Contains <c>T*****FF*</c> | Within <c>T*F**F***</c> | Touches | Overlaps | Crosses | Intersects |
/// | --- | --- | --- | --- | --- | --- | --- | --- |
/// | the square itself | <c>TFFFTFFFT</c> | T | T | F | F | F | T |
/// | square (2,2)-(4,4), inside | <c>TTTFFTFFT</c> | T | F | F | F | F | T |
/// | square (5,5)-(15,15), overlapping | <c>TTTTTTTTT</c> | F | F | F | T | F | T |
/// | square (0,0)-(4,4), sharing the corner and two edges | <c>TTTFTTFFT</c> | T | F | F | F | F | T |
/// | square (0,10)-(10,20), sharing only an edge | <c>FFTFTTTTT</c> | F | F | T | F | F | T |
/// | line (0,5)-(20,5), crossing | <c>TFTTTTTTT</c> | F | F | F | F | T | T |
/// | line (2,2)-(8,8), inside | <c>TTTFFTFFT</c> | T | F | F | F | F | T |
/// | line (0,0)-(0,10), lying on the boundary | <c>FFTTTTFFT</c> | F | F | T | F | F | T |
/// | point (5,5), inside | <c>TFTFFTFFT</c> | T | F | F | F | F | T |
/// | point (0,5), on the boundary | <c>FFTTFTFFT</c> | F | F | T | F | F | T |
/// | point (20,20), outside | <c>FFTFFTTFT</c> | F | F | F | F | F | F |
/// | square (20,20)-(30,30), disjoint | <c>FFTFFTTTT</c> | F | F | F | F | F | F |
///
/// The three reproduction cases the envelope approximation failed are the
/// ones with a geometry on the boundary: <c>Contains</c> and <c>Within</c>
/// read a point or line lying on the container's boundary as contained (the
/// envelope test passes and the intersection covers the containee), and
/// <c>Touches</c> rejects a point on the boundary (the intersection
/// degenerates to a point, which the old code then read as a containment).
///
/// <c>Intersects</c> reads true wherever any of the four matrix positions is
/// <c>T</c> — interior∩interior, either interior against the other's
/// boundary, or boundary∩boundary — so the only false rows are the two whose
/// geometries share nothing at all. That is the OGC intersect pattern union
/// (<c>T******** | *T******* | ***T***** | ****T****</c>), and
/// <c>Intersects_never_builds_the_intersection_geometry</c> pins it as the
/// answer the query path gives without building the intersection.
/// </summary>
public sealed class FeatureSpatialRelationTests
{
    private static readonly NtsGeometryOperations Operations = new();
    private static readonly NtsGeometryRelations Relations = new();

    private static QueryServices Services { get; } = new(
        Operations,
        Relations,
        new NtsGeometryMeasures(),
        new ProjNetTransforms(),
        new ProjNetTransforms());

    private static readonly FeatureSchema Schema = new(
        [new FieldDefinition("shape", AttributeKind.Geometry, nullable: true)]);

    [Theory]
    // A feature polygon containing a query polygon, a point or a line.
    [InlineData("square-equal", "esriSpatialRelContains", true)]
    [InlineData("square-equal", "esriSpatialRelWithin", true)]
    [InlineData("square-inner", "esriSpatialRelContains", true)]
    [InlineData("square-inner", "esriSpatialRelWithin", false)]
    [InlineData("square-overlap", "esriSpatialRelContains", false)]
    [InlineData("square-overlap", "esriSpatialRelWithin", false)]
    [InlineData("square-corner", "esriSpatialRelContains", true)]
    [InlineData("square-corner", "esriSpatialRelWithin", false)]
    [InlineData("square-above", "esriSpatialRelContains", false)]
    [InlineData("square-above", "esriSpatialRelWithin", false)]
    [InlineData("line-crossing", "esriSpatialRelContains", false)]
    [InlineData("line-crossing", "esriSpatialRelWithin", false)]
    [InlineData("line-inside", "esriSpatialRelContains", true)]
    [InlineData("line-inside", "esriSpatialRelWithin", false)]
    [InlineData("line-on-boundary", "esriSpatialRelContains", false)]
    [InlineData("line-on-boundary", "esriSpatialRelWithin", false)]
    [InlineData("point-inside", "esriSpatialRelContains", true)]
    [InlineData("point-inside", "esriSpatialRelWithin", false)]
    [InlineData("point-on-boundary", "esriSpatialRelContains", false)]
    [InlineData("point-on-boundary", "esriSpatialRelWithin", false)]
    [InlineData("point-outside", "esriSpatialRelContains", false)]
    [InlineData("point-outside", "esriSpatialRelWithin", false)]
    [InlineData("square-outside", "esriSpatialRelContains", false)]
    [InlineData("square-outside", "esriSpatialRelWithin", false)]
    public async Task Contains_and_within_follow_the_de9im_patterns(string query, string spatialRel, bool expected)
    {
        Assert.Equal(expected, await MatchesAsync(query, spatialRel));
    }

    [Theory]
    [InlineData("square-equal", false)]
    [InlineData("square-inner", false)]
    [InlineData("square-overlap", false)]
    [InlineData("square-corner", false)]
    [InlineData("square-above", true)]
    [InlineData("line-crossing", false)]
    [InlineData("line-inside", false)]
    [InlineData("line-on-boundary", true)]
    [InlineData("point-inside", false)]
    [InlineData("point-on-boundary", true)]
    [InlineData("point-outside", false)]
    [InlineData("square-outside", false)]
    public async Task Touches_needs_disjoint_interiors_and_meeting_boundaries(string query, bool expected)
    {
        Assert.Equal(expected, await MatchesAsync(query, EsriFeatureQuery.Touches));
    }

    /// <summary>
    /// The reproduction for SpatialEngine-u2x.35: the two-mask
    /// <c>Touches</c> asked for <c>F***T****</c> (boundaries meet) or, when a
    /// point was involved, <c>F**T*****</c> (the point sits on the other's
    /// boundary). Both read the matrix with the feature on the left, so a
    /// point or line FEATURE lying on a query polygon's boundary was missed
    /// twice over: the contact shows up in position 2 (the point's or line's
    /// interior against the query's boundary) — a point's own boundary is
    /// empty, and a line along the edge has its interior on the query's
    /// boundary — and neither mask covers position 2. The reverse operand
    /// order was already served by <c>F**T*****</c>, which is why the
    /// envelope-driven rows above never saw it.
    /// </summary>


    [Theory]
    [InlineData("point-on-boundary", "square-equal", true)]
    [InlineData("line-collinear", "square-equal", true)]
    [InlineData("line-edge", "square-equal", true)]
    [InlineData("square-equal", "point-on-boundary", true)]
    [InlineData("square-equal", "line-collinear", true)]
    [InlineData("point-on-boundary", "square-outside", false)]
    [InlineData("line-collinear", "square-outside", false)]
    public async Task Touches_reads_a_point_or_line_feature_on_a_query_polygon_boundary(
        string feature, string query, bool expected)
    {
        Assert.Equal(expected, await MatchesAsync(feature, query, EsriFeatureQuery.Touches));
    }

    /// <summary>
    /// The served <c>Touches</c> against the OGC touches masks, which are
    /// three patterns and one union: interiors disjoint, and the contact
    /// showing up in position 2, 4 or 5. JTS selects one of the three by
    /// dimension; the served predicate is the dimension-free union of all
    /// three, so every polygon/line/point pair in both operand orders has to
    /// agree with the union the spec states.
    /// </summary>
    [Theory]
    [MemberData(nameof(GeometryPairs))]
    public async Task Touches_agrees_with_the_ogc_touches_masks_on_every_pair(string feature, string query)
    {
        var expected = OgcTouchesMasks.Any(mask => Relations.Relate(
            QueryGeometry(feature), QueryGeometry(query), mask, CancellationToken.None));

        Assert.Equal(expected, await MatchesAsync(feature, query, EsriFeatureQuery.Touches));
    }

    /// <summary>
    /// The three OGC touches masks, spelled out here as the spec states them
    /// so the cross-check is not the served constant compared with itself:
    /// the feature's boundary reaching the query's interior (position 2), the
    /// mirror (position 4), and the two boundaries meeting (position 5).
    /// </summary>
    private static readonly string[] OgcTouchesMasks = ["FT*******", "F**T*****", "F***T****"];

    /// <summary>Every ordered pair of the polygon, line and point fixtures.</summary>
    public static TheoryData<string, string> GeometryPairs()
    {
        var names = new[]
        {
            "square-equal", "square-inner", "square-overlap", "square-corner", "square-above", "square-outside",
            "line-crossing", "line-inside", "line-on-boundary", "line-collinear", "line-edge",
            "point-inside", "point-on-boundary", "point-vertex", "point-outside",
        };
        var pairs = new TheoryData<string, string>();
        foreach (var feature in names)
        {
            foreach (var query in names)
            {
                pairs.Add(feature, query);
            }
        }

        return pairs;
    }

    [Theory]
    [InlineData("square-equal", false)]
    [InlineData("square-inner", false)]
    [InlineData("square-overlap", true)]
    [InlineData("square-corner", false)]
    [InlineData("square-above", false)]
    [InlineData("line-crossing", false)]
    [InlineData("line-inside", false)]
    [InlineData("line-on-boundary", false)]
    [InlineData("point-inside", false)]
    [InlineData("point-on-boundary", false)]
    [InlineData("point-outside", false)]
    [InlineData("square-outside", false)]
    public async Task Overlaps_needs_equal_dimensions_and_a_partial_overlap(string query, bool expected)
    {
        Assert.Equal(expected, await MatchesAsync(query, EsriFeatureQuery.Overlaps));
    }

    [Theory]
    [InlineData("square-equal", false)]
    [InlineData("square-inner", false)]
    [InlineData("square-overlap", false)]
    [InlineData("square-corner", false)]
    [InlineData("square-above", false)]
    [InlineData("line-crossing", true)]
    [InlineData("line-inside", false)]
    [InlineData("line-on-boundary", false)]
    [InlineData("point-inside", false)]
    [InlineData("point-on-boundary", false)]
    [InlineData("point-outside", false)]
    [InlineData("square-outside", false)]
    public async Task Crosses_needs_a_mixed_dimension_meeting(string query, bool expected)
    {
        Assert.Equal(expected, await MatchesAsync(query, EsriFeatureQuery.Crosses));
    }

    [Theory]
    [InlineData("square-equal", true)]
    [InlineData("square-inner", true)]
    [InlineData("square-overlap", true)]
    [InlineData("square-corner", true)]
    [InlineData("square-above", true)]
    [InlineData("line-crossing", true)]
    [InlineData("line-inside", true)]
    [InlineData("line-on-boundary", true)]
    [InlineData("point-inside", true)]
    [InlineData("point-on-boundary", true)]
    [InlineData("point-outside", false)]
    [InlineData("square-outside", false)]
    public async Task Intersects_is_a_non_empty_intersection(string query, bool expected)
    {
        Assert.Equal(expected, await MatchesAsync(query, EsriFeatureQuery.Intersects));
    }

    /// <summary>
    /// The reproduction for SpatialEngine-51k: <c>Intersects</c> is the
    /// intersection <em>test</em>, not the intersection <em>geometry</em>.
    /// The query path runs per feature, so materialising
    /// <c>Intersection(feature, query)</c> for every candidate the envelope
    /// pre-filter admits is a cost the predicate never needed — the OGC
    /// intersect patterns answer the same question over
    /// <see cref="IGeometryRelations.Relate"/>.
    ///
    /// The relation face handed to the match records the patterns it is
    /// asked, so this test fails if the path stops asking the OGC intersect
    /// patterns: the match still has to answer, and answer true or false, out
    /// of the union alone.
    /// </summary>
    [Theory]
    [InlineData("square-equal", true)]
    [InlineData("square-corner", true)]
    [InlineData("line-on-boundary", true)]
    [InlineData("point-on-boundary", true)]
    [InlineData("point-inside", true)]
    [InlineData("point-outside", false)]
    [InlineData("square-outside", false)]
    public async Task Intersects_never_builds_the_intersection_geometry(string query, bool expected)
    {
        var parsed = await QueryAsync(query, EsriFeatureQuery.Intersects);
        var relations = new RecordsPatterns(Relations);
        var candidate = new FeatureSpatialMatcher.MatchCandidate(
            parsed, Feature(Square(0, 0, 10, 10)), 1, parsed.Geometry, relations);

        Assert.Equal(expected, FeatureSpatialMatcher.Matches(candidate, CancellationToken.None));
        Assert.All(relations.Patterns, pattern => Assert.Contains(pattern, IntersectsPatterns));

        // The envelope pre-filter is the cheap half of the answer: a pair
        // whose envelopes are disjoint never reaches the pattern table at
        // all, and a pair that meets always does.
        if (expected)
        {
            Assert.NotEmpty(relations.Patterns);
        }
        else
        {
            Assert.Empty(relations.Patterns);
        }
    }

    /// <summary>
    /// The reason the query path cannot build an intersection to answer
    /// <c>Intersects</c>: it is handed no geometry-operations face at all
    /// (SpatialEngine-51k), so the predicate reads the pattern table or it
    /// does not answer. The matcher shares this one record with the Feature
    /// Service, the Image Service catalog query and the relationship
    /// traversal, so the guarantee holds on every path that matches a
    /// feature.
    /// </summary>
    [Fact]
    public void The_match_candidate_carries_no_geometry_operations_face()
    {
        var faces = typeof(FeatureSpatialMatcher.MatchCandidate)
            .GetProperties()
            .Select(property => property.PropertyType)
            .ToArray();

        Assert.DoesNotContain(typeof(IGeometryOperations), faces);
        Assert.Contains(typeof(IGeometryRelations), faces);
    }

    /// <summary>The pattern union and the intersection test agree on the whole matrix.</summary>
    [Theory]
    [InlineData("square-equal")]
    [InlineData("square-inner")]
    [InlineData("square-overlap")]
    [InlineData("square-corner")]
    [InlineData("square-above")]
    [InlineData("line-crossing")]
    [InlineData("line-inside")]
    [InlineData("line-on-boundary")]
    [InlineData("point-inside")]
    [InlineData("point-on-boundary")]
    [InlineData("point-outside")]
    [InlineData("square-outside")]
    public async Task Intersects_the_pattern_union_agrees_with_the_intersection_test(string query)
    {
        var parsed = await QueryAsync(query, EsriFeatureQuery.Intersects);
        var built = !Operations.Intersection(Square(0, 0, 10, 10), QueryGeometry(query), CancellationToken.None).IsEmpty;

        Assert.Equal(built, await MatchesAsync(query, EsriFeatureQuery.Intersects));
    }

    [Fact]
    public async Task A_cancelled_intersects_match_stops_before_the_exact_predicate()
    {
        var parsed = await QueryAsync("square-equal", EsriFeatureQuery.Intersects);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        Assert.Throws<OperationCanceledException>(() => FeatureSpatialMatcher.Matches(
            new FeatureSpatialMatcher.MatchCandidate(
                parsed, Feature(Square(0, 0, 10, 10)), 1, Square(0, 0, 5, 5), Relations),
            cancelled.Token));
    }

    [Theory]
    [InlineData("point-outside", false)]
    [InlineData("square-outside", false)]
    [InlineData("point-inside", true)]
    [InlineData("line-inside", true)]
    public async Task Envelope_intersects_stays_an_envelope_test(string query, bool expected)
    {
        Assert.Equal(expected, await MatchesAsync(query, EsriFeatureQuery.EnvelopeIntersects));
    }

    [Fact]
    public async Task An_unsupported_spatial_rel_is_a_named_invalid_parameters_failure()
    {
        var query = (await QueryAsync("square-equal", EsriFeatureQuery.Contains)) with { SpatialRel = "esriSpatialRelDisjoint" };
        var error = Assert.Throws<EsriInteropException>(
            () => FeatureSpatialMatcher.Matches(Candidate(query), CancellationToken.None));
        Assert.Equal(EsriErrorCodes.InvalidParameters, error.Code);
        Assert.Contains("esriSpatialRelDisjoint", error.Message);
    }

    [Fact]
    public async Task A_feature_without_a_geometry_never_matches()
    {
        var query = await QueryAsync("square-equal", EsriFeatureQuery.Contains);
        var feature = new Feature(new FeatureId("empty"), Schema, [AttributeValue.Null]);
        Assert.False(FeatureSpatialMatcher.Matches(
            new FeatureSpatialMatcher.MatchCandidate(query, feature, 1, Square(0, 0, 10, 10), Services.Relations),
            CancellationToken.None));
    }

    [Fact]
    public async Task A_cancelled_match_stops_before_the_exact_predicate()
    {
        var query = await QueryAsync("square-equal", EsriFeatureQuery.Contains);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        Assert.Throws<OperationCanceledException>(() => FeatureSpatialMatcher.Matches(
            new FeatureSpatialMatcher.MatchCandidate(query, Feature(Square(0, 0, 10, 10)), 1, Square(0, 0, 5, 5), Services.Relations),
            cancelled.Token));
    }

    private static async Task<bool> MatchesAsync(string query, string spatialRel)
    {
        var parsed = await QueryAsync(query, spatialRel);
        return FeatureSpatialMatcher.Matches(Candidate(parsed), CancellationToken.None);
    }

    /// <summary>
    /// The match with the feature geometry named separately from the query
    /// geometry, so a fixture can put the polygon, the line or the point in
    /// either operand position: the matrix is read with the feature on the
    /// left, and the defect was only visible in one of the two orders.
    /// </summary>
    private static async Task<bool> MatchesAsync(string feature, string query, string spatialRel)
    {
        var parsed = await QueryAsync(query, spatialRel);
        var candidate = new FeatureSpatialMatcher.MatchCandidate(
            parsed, Feature(QueryGeometry(feature)), 1, parsed.Geometry, Services.Relations);
        return FeatureSpatialMatcher.Matches(candidate, CancellationToken.None);
    }

    private static FeatureSpatialMatcher.MatchCandidate Candidate(EsriFeatureQuery query) =>
        new(query, Feature(Square(0, 0, 10, 10)), 1, query.Geometry, Services.Relations);

    private static async Task<EsriFeatureQuery> QueryAsync(string geometry, string spatialRel)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = QueryString.Create(
            [new KeyValuePair<string, string?>("spatialRel", spatialRel)]);
        var parameters = await EsriRequestParameters.ReadAsync(context, CancellationToken.None);
        return EsriFeatureQuery.Parse(parameters, fallback: null) with { Geometry = QueryGeometry(geometry) };
    }

    private static Feature Feature(IGeometry geometry) =>
        new(new FeatureId("feature"), Schema, [AttributeValue.FromGeometry(geometry)]);

    /// <summary>
    /// The OGC intersect patterns the <c>Intersects</c> predicate is defined
    /// by: the interiors meet, either interior reaches the other's boundary,
    /// or the boundaries meet. Spelled out here only so a test can assert the
    /// match path asks for one of these and no other.
    /// </summary>
    private static readonly string[] IntersectsPatterns =
        ["T********", "*T*******", "***T*****", "****T****"];

    /// <summary>
    /// The relation face that remembers which patterns it was asked, so a
    /// test can see that the match reached its answer through the pattern
    /// table.
    /// </summary>
    private sealed class RecordsPatterns(IGeometryRelations inner) : IGeometryRelations
    {
        internal List<string> Patterns { get; } = [];

        public bool Relate(IGeometry left, IGeometry right, string intersectionPattern, CancellationToken cancellationToken = default)
        {
            Patterns.Add(intersectionPattern);
            return inner.Relate(left, right, intersectionPattern, cancellationToken);
        }
    }

    /// <summary>The fixture's query geometries, keyed by the name the matrix table uses.</summary>
    private static IGeometry QueryGeometry(string name) => name switch
    {
        "square-equal" => Square(0, 0, 10, 10),
        "square-inner" => Square(2, 2, 4, 4),
        "square-overlap" => Square(5, 5, 15, 15),
        "square-corner" => Square(0, 0, 4, 4),
        "square-above" => Square(0, 10, 10, 20),
        "line-crossing" => Line(0, 5, 20, 5),
        "line-inside" => Line(2, 2, 8, 8),
        "line-on-boundary" => Line(0, 0, 0, 10),
        "line-collinear" => Line(-5, 0, 15, 0),
        "line-edge" => Line(0, 0, 10, 0),
        "point-inside" => GeometryFactory.CreatePoint(5, 5),
        "point-on-boundary" => GeometryFactory.CreatePoint(0, 5),
        "point-vertex" => GeometryFactory.CreatePoint(0, 0),
        "point-outside" => GeometryFactory.CreatePoint(20, 20),
        "square-outside" => Square(20, 20, 30, 30),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "unknown fixture geometry"),
    };

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
