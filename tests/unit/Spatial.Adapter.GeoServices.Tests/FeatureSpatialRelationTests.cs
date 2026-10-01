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
/// position 9 exterior∩exterior; <c>F</c> is "empty" and <c>T</c>, <c>0</c>,
/// <c>1</c> and <c>2</c> name the dimension of a point, a curve or an area
/// (ADR-0165).
///
/// | Query geometry | DE-9IM | Contains <c>T*****FF*</c> | Within <c>T*F**F***</c> | Touches | Overlaps | Crosses | Intersects |
/// | --- | --- | --- | --- | --- | --- | --- | --- |
/// | the square itself | <c>2FFF1FFF2</c> | T | T | F | F | F | T |
/// | square (2,2)-(4,4), inside | <c>212FF1FF2</c> | T | F | F | F | F | T |
/// | square (5,5)-(15,15), overlapping | <c>212101212</c> | F | F | F | T | F | T |
/// | square (0,0)-(4,4), sharing the corner and two edges | <c>212F11FF2</c> | T | F | F | F | F | T |
/// | square (0,10)-(10,20), sharing only an edge | <c>FF2F11212</c> | F | F | T | F | F | T |
/// | line (0,5)-(20,5), crossing | <c>1F2001102</c> | F | F | F | F | T | T |
/// | line (2,2)-(8,8), inside | <c>102FF1FF2</c> | T | F | F | F | F | T |
/// | line (0,0)-(0,10), lying on the boundary | <c>FF2101FF2</c> | F | F | T | F | F | T |
/// | point (5,5), inside | <c>0F2FF1FF2</c> | T | F | F | F | F | T |
/// | point (0,5), on the boundary | <c>FF20F1FF2</c> | F | F | T | F | F | T |
/// | point (20,20), outside | <c>FF2FF10F2</c> | F | F | F | F | F | F |
/// | square (20,20)-(30,30), disjoint | <c>FF2FF1212</c> | F | F | F | F | F | F |
/// | line (5,0)-(20,0), along the edge and past it | <c>FF2101102</c> | F | F | T | F | F | T |
///
/// The three reproduction cases the envelope approximation failed are the
/// ones with a geometry on the boundary: <c>Contains</c> and <c>Within</c>
/// read a point or line lying on the container's boundary as contained (the
/// envelope test passes and the intersection covers the containee), and
/// <c>Touches</c> rejects a point on the boundary (the intersection
/// degenerates to a point, which the old code then read as a containment).
///
/// <c>Overlaps</c> and <c>Crosses</c> are the two dimension-dependent verbs,
/// and both are read off a per-dimension-pair table rather than one pattern:
/// a line/line pair is the case a single pattern gets wrong in both
/// directions (SpatialEngine-u2x.56).
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

    /// <summary>
    /// Every served verb against every hand-computed row of the DE-9IM
    /// matrix above, read from <see cref="SpatialRelationMatrix"/> — the one
    /// verdict table the Geometry Service <c>relation</c> operation is
    /// measured against as well, so a value is written down once and both
    /// surfaces are held to it (SpatialEngine-dih).
    /// </summary>
    [Theory]
    [MemberData(nameof(SpatialRelationMatrix.MatrixCases), MemberType = typeof(SpatialRelationMatrix))]
    public async Task Named_relations_follow_the_de9im_patterns(string query, string spatialRel, bool expected)
    {
        Assert.Equal(expected, await MatchesAsync(SpatialRelationMatrix.Feature, query, spatialRel));
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

    /// <summary>
    /// Every ordered pair of the polygon, line and point fixtures, from the
    /// one fixture list <see cref="SpatialRelationMatrix"/> holds, so the
    /// query path and the Geometry Service <c>relation</c> operation are
    /// measured over the same pairs in both operand orders.
    /// </summary>
    public static TheoryData<string, string> GeometryPairs() => SpatialRelationMatrix.Pairs();

    /// <summary>
    /// The reproduction for SpatialEngine-u2x.56, <c>Overlaps</c> half: the
    /// served predicate carried one pattern, <c>T*T***T**</c>, for every
    /// same-dimension pair. That pattern asks only that the interiors meet,
    /// and two crossing lines meet in a single point — matrix
    /// <c>0F1FF0102</c> — so the pair read as a partial overlap and the
    /// same-dimension gate let it through. The line/line reading is the
    /// OGC <c>1*T***T**</c>, where the interiors must meet in dimension one:
    /// a shared span overlaps, a crossing does not. The crossing pair is
    /// read in both operand orders, because the matrix is not symmetric.
    /// </summary>
    [Theory]
    [InlineData("line-crossing", "line-inside", false)]
    [InlineData("line-inside", "line-crossing", false)]
    [InlineData("line-edge", "line-shifted-collinear", true)]
    [InlineData("line-shifted-collinear", "line-edge", true)]
    public async Task Overlaps_separates_a_line_pair_that_shares_a_span_from_one_that_crosses(
        string feature, string query, bool expected)
    {
        Assert.Equal(expected, await MatchesAsync(feature, query, EsriFeatureQuery.Overlaps));
    }

    /// <summary>
    /// The reproduction for SpatialEngine-u2x.56, <c>Crosses</c> half: the
    /// dimension gate returned <c>null</c> for equal dimensions, on the
    /// reading that crosses is only a mixed-dimension relation. Two crossing
    /// lines do cross — JTS reads the line/line case as <c>0********</c> —
    /// so the gate answered "never" for a pair the reference answers true.
    /// A collinear pair that shares a span is the other half of the row: it
    /// <c>Overlaps</c> and is not <c>Crosses</c>, which is exactly why the
    /// line/line pattern asks for a dimension-zero interior intersection.
    /// </summary>
    [Theory]
    [InlineData("line-crossing", "line-inside", true)]
    [InlineData("line-inside", "line-crossing", true)]
    [InlineData("line-edge", "line-shifted-collinear", false)]
    [InlineData("line-shifted-collinear", "line-edge", false)]
    public async Task Crosses_reads_two_crossing_lines_in_both_orders(
        string feature, string query, bool expected)
    {
        Assert.Equal(expected, await MatchesAsync(feature, query, EsriFeatureQuery.Crosses));
    }

    /// <summary>
    /// The served <c>Overlaps</c> and <c>Crosses</c> against the reference's
    /// own per-dimension patterns over every ordered pair of the fixtures.
    /// Both verbs are dimension-keyed, so a single pattern cannot serve them:
    /// JTS selects one of several by the pair's dimensions, which is how a
    /// crossing line pair came to read as an overlap and a crossing line
    /// pair as no relation at all (SpatialEngine-u2x.56).
    /// </summary>
    [Theory]
    [MemberData(nameof(GeometryPairs))]
    public async Task Overlaps_agrees_with_the_reference_pattern_on_every_pair(string feature, string query)
    {
        var expected = ReferenceMasks(OgcOverlapsMasks, Dimension(feature), Dimension(query))
            .Any(pattern => Relations.Relate(
                QueryGeometry(feature), QueryGeometry(query), pattern, CancellationToken.None));

        Assert.Equal(expected, await MatchesAsync(feature, query, EsriFeatureQuery.Overlaps));
    }

    /// <summary>The <c>Crosses</c> half of the cross-check over every ordered pair.</summary>
    [Theory]
    [MemberData(nameof(GeometryPairs))]
    public async Task Crosses_agrees_with_the_reference_pattern_on_every_pair(string feature, string query)
    {
        var expected = ReferenceMasks(OgcCrossesMasks, Dimension(feature), Dimension(query))
            .Any(pattern => Relations.Relate(
                QueryGeometry(feature), QueryGeometry(query), pattern, CancellationToken.None));

        Assert.Equal(expected, await MatchesAsync(feature, query, EsriFeatureQuery.Crosses));
    }

    /// <summary>
    /// The reference's dimension-keyed <c>Overlaps</c> patterns, spelled out
    /// here as the reference states them so the cross-check is not the served
    /// table compared with itself: a surface/surface partial overlap, and the
    /// line/line case, where the interiors must meet in dimension one. There
    /// is no point/point or mixed-dimension reading — two points either
    /// coincide or share nothing, so the pair is never an overlap.
    /// </summary>
    private static readonly (int Feature, int Query, string Pattern)[] OgcOverlapsMasks =
    [
        (2, 2, "T*T***T**"),
        (1, 1, "1*T***T**"),
    ];

    /// <summary>
    /// The reference's dimension-keyed <c>Crosses</c> patterns: the two
    /// mixed-dimension cases, where the lower-dimensional geometry's interior
    /// reaches the higher-dimensional one's boundary, and the line/line case,
    /// where the interiors meet in a point. Nothing that involves a point
    /// crosses — a point in an interior is within it and a point on a
    /// boundary touches it — so there is no reading at dimension zero.
    /// </summary>
    private static readonly (int Feature, int Query, string Pattern)[] OgcCrossesMasks =
    [
        (2, 1, "T**T*****"),
        (1, 2, "T*T******"),
        (1, 1, "0********"),
    ];

    /// <summary>
    /// The patterns the reference states for a dimension pair, or none when it
    /// states none: an empty answer is how a verb reads false for a pair
    /// whose dimensions the reference does not relate at all.
    /// </summary>
    private static string[] ReferenceMasks((int Feature, int Query, string Pattern)[] masks, int feature, int query) =>
        masks.Where(mask => mask.Feature == feature && mask.Query == query).Select(mask => mask.Pattern).ToArray();

    /// <summary>The topological dimension of a fixture: points 0, lines 1, areas 2.</summary>
    private static int Dimension(string name) => QueryGeometry(name).Type switch
    {
        GeometryType.Point or GeometryType.MultiPoint => 0,
        GeometryType.LineString or GeometryType.MultiLineString => 1,
        _ => 2,
    };

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
            parsed, Feature(UnitSquare()), 1, parsed.Geometry, relations);

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
        var built = !Operations.Intersection(UnitSquare(), QueryGeometry(query), CancellationToken.None).IsEmpty;

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
                parsed, Feature(UnitSquare()), 1, Square(0, 0, 5, 5), Relations),
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
            new FeatureSpatialMatcher.MatchCandidate(query, feature, 1, UnitSquare(), Services.Relations),
            CancellationToken.None));
    }

    [Fact]
    public async Task A_cancelled_match_stops_before_the_exact_predicate()
    {
        var query = await QueryAsync("square-equal", EsriFeatureQuery.Contains);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        Assert.Throws<OperationCanceledException>(() => FeatureSpatialMatcher.Matches(
            new FeatureSpatialMatcher.MatchCandidate(query, Feature(UnitSquare()), 1, Square(0, 0, 5, 5), Services.Relations),
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
    /// left, and the defect was only visible in one of the two orders. The
    /// Geometry Service's own cross-surface test asks this, so one surface
    /// answering a named verb differently from the other fails with the pair
    /// and the verb in the test name (SpatialEngine-dih).
    /// </summary>
    internal static async Task<bool> MatchesAsync(string feature, string query, string spatialRel)
    {
        var parsed = await QueryAsync(query, spatialRel);
        var candidate = new FeatureSpatialMatcher.MatchCandidate(
            parsed, Feature(QueryGeometry(feature)), 1, parsed.Geometry, Services.Relations);
        return FeatureSpatialMatcher.Matches(candidate, CancellationToken.None);
    }

    private static FeatureSpatialMatcher.MatchCandidate Candidate(EsriFeatureQuery query) =>
        new(query, Feature(UnitSquare()), 1, query.Geometry, Services.Relations);

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

    /// <summary>
    /// The fixture's query geometries, keyed by the name the matrix table
    /// uses: the one fixture definition <see cref="SpatialRelationMatrix"/>
    /// holds, projected into the engine's own geometry values (SpatialEngine-dih).
    /// </summary>
    private static IGeometry QueryGeometry(string name) => SpatialRelationMatrix.Of(name).Geometry;

    /// <summary>
    /// The feature geometry the matrix is read against — the unit square,
    /// from the same fixture table the query geometries come from.
    /// </summary>
    private static IGeometry UnitSquare() => QueryGeometry(SpatialRelationMatrix.Feature);

    /// <summary>
    /// The reproduction for SpatialEngine-imj, in the query path: every
    /// pattern the adapter serves, answered from the pair's <em>exact
    /// intersection matrix</em> rather than by asking
    /// <see cref="IGeometryRelations.Relate"/> the same pattern again. The
    /// two agreement tests above ask the engine with the reference's masks
    /// and compare the answers, which is a comparison between the served
    /// table and a second copy of it; this one reads the matrix — nine
    /// independent single-cell questions, so no pattern matcher is involved
    /// in the expectation — and reads the served pattern over it, cell by
    /// cell, with a matcher written here.
    ///
    /// It is deliberately kept beside <see cref="SpatialRelationMatrix"/>
    /// rather than folded into it (ADR-0156). The matrix is where a value is
    /// written down once so both Esri surfaces are held to it, and what it
    /// holds is the hand-computed matrix and the verdict columns — which is
    /// to say it is the served table's own second copy, and a table cannot
    /// cross-check itself. This test is the other kind of check: it derives
    /// the expected matrix from the geometry and reads the served patterns
    /// over that derivation, so a mistake in the served table, in the
    /// hand-written verdicts, or in the reference masks is caught here rather
    /// than agreeing with itself. One table is not enough; two readings from
    /// different directions is the whole of it.
    ///
    /// It is the check that would have caught a served pattern naming a
    /// different cell than the one it means: the report that opened the bead
    /// read a pattern and its exact matrix as disagreeing when the pattern
    /// constrained a cell the matrix filled differently. The table is
    /// therefore pinned to DE-9IM, over every ordered pair of the fixtures,
    /// and the served pattern strings are pinned beside the answers.
    /// </summary>
    [Theory]
    [MemberData(nameof(GeometryPairs))]
    public async Task Every_served_pattern_agrees_with_the_pairs_exact_matrix(string feature, string query)
    {
        var matrix = ExactMatrix(feature, query);
        var expectations = new (string SpatialRel, bool Expected)[]
        {
            (EsriFeatureQuery.Contains, Reads(matrix, ContainsPattern)),
            (EsriFeatureQuery.Within, Reads(matrix, WithinPattern)),
            (EsriFeatureQuery.Touches, OgcTouchesMasks.Any(mask => Reads(matrix, mask))),
            (EsriFeatureQuery.Overlaps, ReferenceMasks(OgcOverlapsMasks, Dimension(feature), Dimension(query))
                .Any(pattern => Reads(matrix, pattern))),
            (EsriFeatureQuery.Crosses, ReferenceMasks(OgcCrossesMasks, Dimension(feature), Dimension(query))
                .Any(pattern => Reads(matrix, pattern))),
            (EsriFeatureQuery.Intersects, IntersectsPatterns.Any(pattern => Reads(matrix, pattern))),
        };

        foreach (var (spatialRel, expected) in expectations)
        {
            Assert.Equal(expected, await MatchesAsync(feature, query, spatialRel));
        }
    }

    /// <summary>
    /// The other half of keeping the oracle, and the half
    /// <see cref="SpatialRelationMatrix"/> cannot do for itself: the table's
    /// <c>De9im</c> column is written down by hand, and before this the
    /// hand-written column was read by nothing at all — the verdict columns
    /// are what the two surfaces are held to, so a wrong character in a
    /// hand-computed matrix would have gone unnoticed while still being the
    /// row every reader of that table trusts. Here the same nine single-cell
    /// questions derive the matrix from the geometry and the hand-written
    /// column has to agree with them, so the column is checked by something
    /// that did not write it (ADR-0156).
    ///
    /// A row the matrix does not carry a verdict for — <c>point-vertex</c>,
    /// whose matrix the reference's own named predicates answer instead — is
    /// not walked here, so the test states only what the table states.
    ///
    /// The comparison is strict: the column is the matrix, so every cell is
    /// written as its dimension — <c>T</c>, <c>F</c>, <c>0</c>, <c>1</c> or
    /// <c>2</c> — and <c>2FFF1FFF2</c> is not the same recorded row as
    /// <c>TFFFTFFFT</c> (ADR-0165).
    /// </summary>
    [Theory]
    [MemberData(nameof(MatrixQueries))]
    public void Every_hand_computed_matrix_is_the_matrix_the_pair_derives(string query)
    {
        var recorded = SpatialRelationMatrix.Verdicts[query].De9im;
        var derived = ExactMatrix(SpatialRelationMatrix.Feature, query);

        Assert.Equal(derived, recorded);
    }

    /// <summary>
    /// The query fixtures the matrix carries a verdict row for, by name: the
    /// rows whose hand-written matrix there is to check.
    /// </summary>
    public static TheoryData<string> MatrixQueries()
    {
        var queries = new TheoryData<string>();
        foreach (var name in SpatialRelationMatrix.Verdicts.Keys)
        {
            queries.Add(name);
        }

        return queries;
    }

    /// <summary>
    /// The served <c>Contains</c> and <c>Within</c> patterns, spelled out
    /// here as the OGC states them so the cross-check above reads the served
    /// constants' intent rather than importing them.
    /// </summary>
    private const string ContainsPattern = "T*****FF*";

    /// <summary>The <c>Within</c> reading: the mirror of <see cref="ContainsPattern"/>.</summary>
    private const string WithinPattern = "T*F**F***";

    /// <summary>
    /// The pair's exact intersection matrix, reconstructed from nine
    /// single-cell questions — "is this cell non-empty?", and when it is,
    /// "is it a point, a curve or an area?". Every cell is asked on its own,
    /// so the expectation in the test above is not read through the pattern
    /// matcher whose behaviour it is checking.
    /// </summary>
    private static string ExactMatrix(string feature, string query)
    {
        var left = QueryGeometry(feature);
        var right = QueryGeometry(query);
        var cells = new char[9];
        for (var position = 0; position < cells.Length; position++)
        {
            cells[position] = ExactCell(left, right, position);
        }

        return new string(cells);
    }

    private static char ExactCell(IGeometry left, IGeometry right, int position)
    {
        if (!Relations.Relate(left, right, At(position, "T"), CancellationToken.None))
        {
            Assert.True(Relations.Relate(left, right, At(position, "F"), CancellationToken.None),
                $"cell {position} is neither empty nor non-empty");
            return 'F';
        }

        return CellDimensions.Single(dimension => Relations.Relate(
            left, right, At(position, dimension.ToString()), CancellationToken.None));
    }

    /// <summary>
    /// Whether a pattern holds over an exact matrix, read from the matrix
    /// string alone: <c>T</c> asks for a non-empty cell whatever its
    /// dimension, <c>F</c> for an empty one, a digit for that dimension and
    /// <c>*</c> for nothing.
    /// </summary>
    private static bool Reads(string matrix, string pattern) =>
        matrix.Zip(pattern, (cell, asked) => asked switch
        {
            '*' => true,
            'T' => cell != 'F',
            'F' => cell == 'F',
            _ => cell == asked,
        }).All(holds => holds);

    /// <summary>The three cell dimensions, asked in that order.</summary>
    private static readonly char[] CellDimensions = ['0', '1', '2'];

    /// <summary>The all-wildcard pattern with one cell replaced by <paramref name="symbol"/>.</summary>
    private static string At(int position, string symbol) =>
        "*********"[..position] + symbol + "*********"[(position + 1)..];


    /// <summary>A square named by its extent, for the shapes the matrix does not carry.</summary>
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
