using Microsoft.AspNetCore.Http;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.Esri.Codec;
using Spatial.Operations.NetTopologySuite;

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
/// | Query geometry | DE-9IM | Contains <c>T*****FF*</c> | Within <c>T*F**F***</c> | Touches | Overlaps | Crosses |
/// | --- | --- | --- | --- | --- | --- | --- |
/// | the square itself | <c>TFFFTFFFT</c> | T | T | F | F | F |
/// | square (2,2)-(4,4), inside | <c>TTTFFTFFT</c> | T | F | F | F | F |
/// | square (5,5)-(15,15), overlapping | <c>TTTTTTTTT</c> | F | F | F | T | F |
/// | square (0,0)-(4,4), sharing the corner and two edges | <c>TTTFTTFFT</c> | T | F | F | F | F |
/// | square (0,10)-(10,20), sharing only an edge | <c>FFTFTTTTT</c> | F | F | T | F | F |
/// | line (0,5)-(20,5), crossing | <c>TFTTTTTTT</c> | F | F | F | F | T |
/// | line (2,2)-(8,8), inside | <c>TTTFFTFFT</c> | T | F | F | F | F |
/// | line (0,0)-(0,10), lying on the boundary | <c>FFTTTTFFT</c> | F | F | T | F | F |
/// | point (5,5), inside | <c>TFTFFTFFT</c> | T | F | F | F | F |
/// | point (0,5), on the boundary | <c>FFTTFTFFT</c> | F | F | T | F | F |
/// | point (20,20), outside | <c>FFTFFTTFT</c> | F | F | F | F | F |
/// | square (20,20)-(30,30), disjoint | <c>FFTFFTTTT</c> | F | F | F | F | F |
///
/// The three reproduction cases the envelope approximation failed are the
/// ones with a geometry on the boundary: <c>Contains</c> and <c>Within</c>
/// read a point or line lying on the container's boundary as contained (the
/// envelope test passes and the intersection covers the containee), and
/// <c>Touches</c> rejects a point on the boundary (the intersection
/// degenerates to a point, which the old code then read as a containment).
/// </summary>
public sealed class FeatureSpatialRelationTests
{
    private static readonly NtsGeometryOperations Operations = new();
    private static readonly NtsGeometryRelations Relations = new();

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
            new FeatureSpatialMatcher.MatchCandidate(query, feature, 1, Square(0, 0, 10, 10), Operations, Relations),
            CancellationToken.None));
    }

    [Fact]
    public async Task A_cancelled_match_stops_before_the_exact_predicate()
    {
        var query = await QueryAsync("square-equal", EsriFeatureQuery.Contains);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        Assert.Throws<OperationCanceledException>(() => FeatureSpatialMatcher.Matches(
            new FeatureSpatialMatcher.MatchCandidate(query, Feature(Square(0, 0, 10, 10)), 1, Square(0, 0, 5, 5), Operations, Relations),
            cancelled.Token));
    }

    private static async Task<bool> MatchesAsync(string query, string spatialRel)
    {
        var parsed = await QueryAsync(query, spatialRel);
        return FeatureSpatialMatcher.Matches(Candidate(parsed), CancellationToken.None);
    }

    private static FeatureSpatialMatcher.MatchCandidate Candidate(EsriFeatureQuery query) =>
        new(query, Feature(Square(0, 0, 10, 10)), 1, query.Geometry, Operations, Relations);

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
        "point-inside" => GeometryFactory.CreatePoint(5, 5),
        "point-on-boundary" => GeometryFactory.CreatePoint(0, 5),
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
