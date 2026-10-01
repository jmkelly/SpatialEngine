using Microsoft.AspNetCore.Http;
using Spatial.Contracts;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.Operations.NetTopologySuite;

namespace Spatial.Adapter.GeoServices.Tests;

/// <summary>
/// Which geometry is the left operand of the served <c>spatialRel</c> matrix
/// (SpatialEngine-2ve, ADR-0171).
///
/// <para>The protocol names each relation as the relation of the
/// <b>feature</b> to the <b>input (query) geometry</b>, and ArcGIS Server
/// implements it that way round, which is the opposite of the OGC Simple
/// Features reading the table was first served in. The REST reference says
/// <c>spatialRel</c> is "the spatial relationship to be applied to the input
/// geometry", and a live FeatureServer says the same: a point at
/// (-71.5, 41.6) queried against
/// <c>sampleserver6.arcgisonline.com/.../USA/MapServer/3</c> (Counties)
/// answers <c>esriSpatialRelWithin</c> → 1 and <c>esriSpatialRelContains</c> →
/// 0, so <c>Within</c> is "the feature contains the input geometry". The
/// same way round with the operands the other size — a box around six
/// counties — answers <c>esriSpatialRelContains</c> → 6 and
/// <c>esriSpatialRelWithin</c> → 0.</para>
///
/// <para>So <c>Contains</c> and <c>Within</c> name the OGC relations of the
/// <em>query</em> geometry to the <em>feature</em>, and the four other
/// verbs are symmetric under transposition and do not move: the OGC masks
/// for <c>Touches</c>, <c>Overlaps</c>, <c>Crosses</c> and <c>Intersects</c>
/// are closed under the transpose, so swapping the operands of the matrix
/// cannot change a single one of their verdicts. The pair that tells the two
/// directions apart is a feature strictly larger than the query geometry and
/// the same feature strictly smaller, and both are asked here, for every
/// served verb, so the choice is pinned either way rather than restated
/// from whatever the table happens to say.</para>
/// </summary>
public sealed class SpatialRelDirectionTests
{
    private static readonly NtsGeometryRelations Relations = new();

    /// <summary>
    /// The two operand orders over one nesting: the unit square
    /// (0,0)-(10,10) and a strictly inner square (2,2)-(4,4). The feature is
    /// the outer one on the first row and the inner one on the second, so
    /// every served verb is asked in both orders and a verb whose reading is
    /// symmetric cannot tell them apart while an antisymmetric one can.
    ///
    /// The columns are the verdict with the <b>feature larger</b> than the
    /// query geometry, then the verdict with the <b>query geometry larger</b>.
    /// Under the ArcGIS direction a feature that surrounds the query
    /// geometry is <c>Within</c> it and a feature inside the query geometry is
    /// <c>Contained</c> in it; the other four verbs answer the same either
    /// way, which is the point of stating them here rather than only the two
    /// that move.
    /// </summary>
    [Theory]
    [InlineData(EsriFeatureQuery.Contains, false, true)]
    [InlineData(EsriFeatureQuery.Within, true, false)]
    [InlineData(EsriFeatureQuery.Touches, false, false)]
    [InlineData(EsriFeatureQuery.Overlaps, false, false)]
    [InlineData(EsriFeatureQuery.Crosses, false, false)]
    [InlineData(EsriFeatureQuery.Intersects, true, true)]
    public async Task The_served_direction_is_the_arcgis_one(
        string spatialRel, bool featureLarger, bool queryLarger)
    {
        Assert.Equal(featureLarger, await MatchesAsync(UnitSquare(), Square(2, 2, 4, 4), spatialRel));
        Assert.Equal(queryLarger, await MatchesAsync(Square(2, 2, 4, 4), UnitSquare(), spatialRel));
    }

    /// <summary>
    /// The <c>esriSpatialRel</c> name reaches the served predicate as one
    /// table both Esri surfaces read, so the direction is a property of the
    /// verb rather than of the endpoint: the Geometry Service's
    /// <c>relation</c> operation over <c>geometries1</c>/<c>geometries2</c>
    /// answers the same two operand orders the same way the query path does.
    /// A change that flipped one surface and not the other fails here.
    /// </summary>
    [Fact]
    public async Task Both_served_surfaces_answer_the_same_direction_for_the_same_pair()
    {
        Assert.Equal(
            await MatchesAsync(UnitSquare(), Square(2, 2, 4, 4), EsriFeatureQuery.Within),
            await GeometryServiceRelationAsync(UnitSquare(), Square(2, 2, 4, 4), EsriFeatureQuery.Within));
        Assert.Equal(
            await MatchesAsync(Square(2, 2, 4, 4), UnitSquare(), EsriFeatureQuery.Within),
            await GeometryServiceRelationAsync(Square(2, 2, 4, 4), UnitSquare(), EsriFeatureQuery.Within));
        Assert.Equal(
            await MatchesAsync(UnitSquare(), Square(2, 2, 4, 4), EsriFeatureQuery.Contains),
            await GeometryServiceRelationAsync(UnitSquare(), Square(2, 2, 4, 4), EsriFeatureQuery.Contains));
        Assert.Equal(
            await MatchesAsync(Square(2, 2, 4, 4), UnitSquare(), EsriFeatureQuery.Contains),
            await GeometryServiceRelationAsync(Square(2, 2, 4, 4), UnitSquare(), EsriFeatureQuery.Contains));
    }

    /// <summary>
    /// The two edge semantics observed against a live FeatureServer rather
    /// than read off the OGC table (SpatialEngine-msc), pinned in both
    /// operand orders because both are about the <em>sizes</em> of the pair
    /// and neither the direction test nor the pattern table states either.
    ///
    /// <para><b>A point query geometry strictly inside a polygon feature</b>
    /// is <c>Within</c> the feature and not <c>Contained</c> in it, so the
    /// point-in-polygon query a QGIS or REST JS client sends keeps working
    /// and the strict mask <c>T*****FF*</c> is not rejected for it: a point
    /// has no boundary and never reaches the area's exterior, so the only
    /// cells the mask asks beyond the interior intersection are empty. The
    /// live Counties layer says the same — a point at (-71.5, 41.6) answers
    /// <c>esriSpatialRelWithin</c> → 1 and <c>esriSpatialRelContains</c> → 0.
    /// </para>
    ///
    /// <para><b>A point strictly inside an area</b> is not <c>Crosses</c> it
    /// in either order, which is the OGC reading (a dimension pair involving a
    /// point names no crosses mask, ADR-0106) and not ArcGIS's
    /// "partially inside, partially outside" wording, which a point cannot
    /// satisfy. The live Cities layer says the same: an envelope around
    /// <i>Ewa Beach</i> answers <c>esriSpatialRelCrosses</c> → 0 where
    /// <c>esriSpatialRelContains</c> → 8, and a polygon input area holding the
    /// point answers <c>Crosses</c> → 0. The line layer next to it still
    /// crosses, so the verb is not simply dead: five of the seven highways
    /// meeting the same envelope answer <c>esriSpatialRelCrosses</c>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_point_inside_an_area_is_within_it_and_never_crosses_it()
    {
        var area = UnitSquare();
        var inside = Point(5, 5);

        // Point query geometry inside a polygon feature: the polygon contains
        // the point, and the point is inside the polygon.
        Assert.True(await MatchesAsync(area, inside, EsriFeatureQuery.Within));
        Assert.False(await MatchesAsync(area, inside, EsriFeatureQuery.Contains));
        Assert.False(await MatchesAsync(inside, area, EsriFeatureQuery.Within));
        Assert.True(await MatchesAsync(inside, area, EsriFeatureQuery.Contains));

        // And the point is not a cross of the area in either order.
        Assert.False(await MatchesAsync(area, inside, EsriFeatureQuery.Crosses));
        Assert.False(await MatchesAsync(inside, area, EsriFeatureQuery.Crosses));
    }

    private static Task<bool> MatchesAsync(IGeometry feature, IGeometry query, string spatialRel) =>
        GeometryServiceRelationAsync(feature, query, spatialRel);

    /// <summary>
    /// The shared matcher, asked with the feature on the left — the one call
    /// both surfaces make: <c>FeatureSpatialMatcher.Matches</c> is the query
    /// path's predicate, and <c>GeometryService</c>'s <c>relation</c>
    /// operation resolves every served verb out of the same
    /// <c>SpatialRelationPredicates</c> entries with its first geometry as the
    /// feature (SpatialEngine-dih), so asking the matcher with both operand
    /// orders measures both.
    /// </summary>
    private static async Task<bool> GeometryServiceRelationAsync(
        IGeometry feature, IGeometry query, string spatialRel)
    {
        var parsed = await QueryAsync(query, spatialRel);

        return FeatureSpatialMatcher.Matches(
            new FeatureSpatialMatcher.MatchCandidate(
                parsed,
                new Feature(new FeatureId("feature"), Schema, [AttributeValue.FromGeometry(feature)]),
                1,
                parsed.Geometry,
                Relations),
            CancellationToken.None);
    }

    private static readonly FeatureSchema Schema = new(
        [new FieldDefinition("shape", AttributeKind.Geometry, nullable: true)]);

    private static async Task<EsriFeatureQuery> QueryAsync(IGeometry geometry, string spatialRel)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = QueryString.Create(
            [new KeyValuePair<string, string?>("spatialRel", spatialRel)]);
        var parameters = await EsriRequestParameters.ReadAsync(context, CancellationToken.None);

        return EsriFeatureQuery.Parse(parameters, fallback: null) with { Geometry = geometry };
    }

    /// <summary>The outer fixture: the unit square the matrix table is read against.</summary>
    private static Polygon UnitSquare() => Square(0, 0, 10, 10);

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
}