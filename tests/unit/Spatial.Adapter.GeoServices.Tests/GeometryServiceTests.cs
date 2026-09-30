using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Spatial.Core.Geometry;
using Spatial.Esri.Codec;
using Spatial.Operations.NetTopologySuite;
using Spatial.Transformations.ProjNet;

namespace Spatial.Adapter.GeoServices.Tests;

/// <summary>
/// The Geometry Service dispatcher (spec §7): each operation maps protocol
/// arguments onto the engine verbs, rejects unsupported modifiers and returns
/// the shaped result. The generalization surface is pinned here for
/// <c>generalize</c> and in <see cref="GeometryServiceSimplifyTests"/> for
/// <c>simplify</c>, whose tolerance arrives as <c>deviation</c>/<c>value</c>.
/// </summary>
public sealed class GeometryServiceTests
{
    private static readonly NtsGeometryOperations Operations = new();
    private static readonly ProjNetTransforms Transforms = new();

    private static readonly GeometryServiceCapabilities Capabilities = new(
        Operations,
        new NtsGeometryMeasures(),
        new NtsGeometryProcessing(),
        new NtsGeometryRelations(),
        Transforms,
        Transforms,
        new ProjNetGeodesicBuffering(Operations, new NtsGeometryProcessing()));

    private static async Task<EsriRequestParameters> ParamsAsync(params (string Key, string Value)[] values)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = QueryString.Create(values.Select(value => new KeyValuePair<string, string?>(value.Key, value.Value)));
        return await EsriRequestParameters.ReadAsync(context, CancellationToken.None);
    }

    private static async Task<JsonElement> ExecuteAsync(IResult result)
    {
        var context = new DefaultHttpContext();
        context.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        context.Response.Body = new MemoryStream();
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        return document.RootElement.Clone();
    }

    private static async Task<JsonElement> DispatchAsync(string operation, params (string Key, string Value)[] values) =>
        await ExecuteAsync(GeometryService.Dispatch(operation, await ParamsAsync(values), Capabilities, CancellationToken.None));

    [Fact]
    public async Task Info_advertises_the_supported_operations()
    {
        var info = await ExecuteAsync(GeometryService.Info());

        Assert.Equal(10.0, info.GetProperty("currentVersion").GetDouble());
        var capabilities = info.GetProperty("capabilities").GetString();
        Assert.Contains("AreasAndLengths", capabilities);
        Assert.Contains("FindTransformations", capabilities);
        Assert.DoesNotContain("GeoCoordinateString", capabilities);
    }

    [Theory]
    [InlineData("rotate")]
    // Recorded non-goals (ADR-0035): the engine has no verb for these, so the
    // facade rejects them explicitly rather than mis-mapping a near-miss.
    [InlineData("offset")]
    [InlineData("cut")]
    [InlineData("reshape")]
    [InlineData("trimExtend")]
    [InlineData("autoComplete")]
    public async Task An_unsupported_operation_is_a_typed_failure(string operation)
    {
        var exception = await Assert.ThrowsAsync<EsriInteropException>(() => DispatchAsync(operation));

        Assert.Equal(EsriErrorCodes.InvalidParameters, exception.Code);
    }

    [Fact]
    public async Task Project_transforms_each_geometry()
    {
        var result = await DispatchAsync("project",
            ("geometries", """[{"x":13.405,"y":52.52,"spatialReference":{"wkid":4326}}]"""),
            ("inSR", "4326"),
            ("outSR", "32632"));

        Assert.True(result.GetProperty("geometries")[0].GetProperty("x").GetDouble() > 100000);
    }

    [Fact]
    public async Task Project_accepts_the_esri_docs_wrapper()
    {
        // Parity playground verbatim: project packages its point as
        // {"geometryType": "...", "geometries": [...]}. The localhost side
        // must project it exactly like the public Esri service instead of
        // failing with "carries none of the Esri shapes".
        var result = await DispatchAsync("project",
            ("geometries", """{"geometryType":"esriGeometryPoint","geometries":[{"x":-117,"y":34}]}"""),
            ("inSR", "4326"),
            ("outSR", "3857"));

        var projected = result.GetProperty("geometries")[0];
        Assert.True(Math.Abs(projected.GetProperty("x").GetDouble() - -13024380.0) < 1000);
        Assert.True(Math.Abs(projected.GetProperty("y").GetDouble() - 4028802.0) < 1000);
    }

    [Fact]
    public async Task Buffer_accepts_the_esri_docs_wrapper()
    {
        // Parity playground verbatim shape (planarised: the engine has no
        // geodesic verb, so the sample buffers planar in the projected
        // bufferSR exactly as the host documents).
        var result = await DispatchAsync("buffer",
            ("geometries", """{"geometryType":"esriGeometryPoint","geometries":[{"x":0,"y":0,"spatialReference":{"wkid":4326}}]}"""),
            ("inSR", "4326"),
            ("bufferSR", "3857"),
            ("outSR", "3857"),
            ("distances", "1000"),
            ("unit", "9001"));

        Assert.Equal(1, result.GetProperty("geometries").GetArrayLength());
    }

    [Fact]
    public async Task Label_points_accepts_the_docs_polygons_alias()
    {
        // Esri-docs verbatim: labelPoints names its input 'polygons', not
        // 'geometries'. The localhost side must answer like the public
        // service instead of demanding 'geometries'.
        var result = await DispatchAsync("labelpoints",
            ("polygons", """[{"rings":[[[0,0],[2,0],[2,2],[0,2],[0,0]]]}]"""),
            ("sr", "4326"));

        Assert.Single(result.GetProperty("geometries").EnumerateArray());
    }

    [Fact]
    public async Task Relation_accepts_the_esri_docs_names()
    {
        // Esri-docs verbatim: geometries1/geometries2 with sr1/sr2 and a
        // named esriSpatialRel* relation. The point sits clearly inside the
        // square (never on its boundary) so both sides answer 1.
        var result = await DispatchAsync("relation",
            ("geometries1", """[{"rings":[[[-118,33],[-116,33],[-116,35],[-118,35],[-118,33]]]}]"""),
            ("geometries2", """[{"x":-117,"y":34}]"""),
            ("sr1", "4326"),
            ("sr2", "4326"),
            ("relation", "esriSpatialRelContains"));

        var relations = result.GetProperty("relations").EnumerateArray().Select(value => value.GetInt32()).ToArray();
        Assert.Equal([1], relations);
    }

    /// <summary>
    /// The named relations the Geometry Service serves out of the one DE-9IM
    /// pattern table the feature query path uses, so the same verb has one
    /// answer whichever endpoint serves it (SpatialEngine-zpz, and
    /// SpatialEngine-51k for <c>Intersects</c>).
    ///
    /// The fixture is the same unit square feature and the same twelve query
    /// geometries whose hand-computed DE-9IM matrix
    /// <see cref="FeatureSpatialRelationTests"/> pins the query path against:
    /// the matrix rows are the feature's components and the columns the
    /// query's, in interior/boundary/exterior order. Every expected value
    /// below is copied from that table's Touches/Overlaps/Crosses/Intersects
    /// columns, so a second copy of a pattern string cannot drift from the
    /// first.
    /// </summary>
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
    public async Task Relation_touches_needs_disjoint_interiors_and_meeting_boundaries(string query, bool expected) =>
        Assert.Equal(expected, await RelatesAsync(query, "esriSpatialRelTouches"));

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
    public async Task Relation_overlaps_needs_equal_dimensions_and_a_partial_overlap(string query, bool expected) =>
        Assert.Equal(expected, await RelatesAsync(query, "esriSpatialRelOverlaps"));

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
    public async Task Relation_crosses_needs_a_mixed_dimension_meeting(string query, bool expected) =>
        Assert.Equal(expected, await RelatesAsync(query, "esriSpatialRelCrosses"));

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
    public async Task Relation_intersects_is_the_ogc_pattern_union(string query, bool expected) =>
        Assert.Equal(expected, await RelatesAsync(query, "esriSpatialRelIntersects"));

    [Fact]
    public async Task Relation_rejects_a_relation_name_it_does_not_serve()
    {
        var error = await Assert.ThrowsAsync<EsriInteropException>(() => DispatchAsync("relation",
            ("geometries1", """[{"rings":[[[0,0],[10,0],[10,10],[0,10],[0,0]]]}]"""),
            ("geometries2", """[{"x":5,"y":5}]"""),
            ("relation", "esriSpatialRelSpans")));

        Assert.Equal(EsriErrorCodes.InvalidParameters, error.Code);
        Assert.Contains("esriSpatialRelSpans", error.Message);
    }

    /// <summary>The unit square against the fixture query geometry, under the named relation.</summary>
    private static async Task<bool> RelatesAsync(string query, string relation)
    {
        var result = await DispatchAsync("relation",
            ("geometries1", """[{"rings":[[[0,0],[10,0],[10,10],[0,10],[0,0]]]}]"""),
            ("geometries2", $"[{QueryGeometry(query)}]"),
            ("relation", relation));

        return result.GetProperty("relations")[0].GetInt32() == 1;
    }

    /// <summary>
    /// The fixture query geometries as Esri JSON, keyed by the names the
    /// DE-9IM matrix table in <see cref="FeatureSpatialRelationTests"/> uses:
    /// the same shapes, so the expected values are read across, not re-derived.
    /// </summary>
    private static string QueryGeometry(string name) => name switch
    {
        "square-equal" => Square(0, 0, 10, 10),
        "square-inner" => Square(2, 2, 4, 4),
        "square-overlap" => Square(5, 5, 15, 15),
        "square-corner" => Square(0, 0, 4, 4),
        "square-above" => Square(0, 10, 10, 20),
        "line-crossing" => Line(0, 5, 20, 5),
        "line-inside" => Line(2, 2, 8, 8),
        "line-on-boundary" => Line(0, 0, 0, 10),
        "point-inside" => Point(5, 5),
        "point-on-boundary" => Point(0, 5),
        "point-outside" => Point(20, 20),
        "square-outside" => Square(20, 20, 30, 30),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "unknown fixture geometry"),
    };

    private static string Square(double minX, double minY, double maxX, double maxY) =>
        $$"""{"rings":[[[{{minX}},{{minY}}],[{{maxX}},{{minY}}],[{{maxX}},{{maxY}}],[{{minX}},{{maxY}}],[{{minX}},{{minY}}]]]}""";

    private static string Line(double x1, double y1, double x2, double y2) =>
        $$"""{"paths":[[[{{x1}},{{y1}}],[{{x2}},{{y2}}]]]}""";

    private static string Point(double x, double y) => $$"""{"x":{{x}},"y":{{y}}}""";

    [Fact]
    public async Task Project_requires_out_sr()
    {
        await Assert.ThrowsAsync<EsriInteropException>(() => DispatchAsync("project", ("geometries", "[]")));
    }

    [Fact]
    public async Task Project_accepts_the_transformation_it_applies_and_names_the_others()
    {
        // findTransformations publishes the ranked candidates; project applies
        // the first of them, so naming it is honoured rather than refused.
        var candidates = await DispatchAsync("findTransformations", ("inSR", "4326"), ("outSR", "27700"));
        var applied = candidates[0].GetProperty("name").GetString();

        var projected = await DispatchAsync("project",
            ("geometries", """[{"x":-0.1276,"y":51.5072,"spatialReference":{"wkid":4326}}]"""),
            ("inSR", "4326"),
            ("outSR", "27700"),
            ("datumTransformation", applied!));

        // PROJ's value at the London control point; the catalogue applies the
        // Helmert path rather than the OSTN15 grid, so the 0.1 m the existing
        // control-point tests document is the tolerance (ADR-0027 accuracy).
        var point = projected.GetProperty("geometries")[0];
        Assert.Equal(530043.194981, point.GetProperty("x").GetDouble(), 0.1);
        Assert.Equal(180358.208620, point.GetProperty("y").GetDouble(), 0.1);
    }

    [Fact]
    public async Task Project_rejects_a_transformation_it_does_not_apply()
    {
        var candidates = await DispatchAsync("findTransformations", ("inSR", "4326"), ("outSR", "27700"));
        var notApplied = candidates[candidates.GetArrayLength() - 1].GetProperty("name").GetString();

        var exception = await Assert.ThrowsAsync<EsriInteropException>(() => DispatchAsync("project",
            ("geometries", """[{"x":-0.1276,"y":51.5072,"spatialReference":{"wkid":4326}}]"""),
            ("inSR", "4326"),
            ("outSR", "27700"),
            ("datumTransformation", notApplied!)));

        Assert.Equal(EsriErrorCodes.InvalidParameters, exception.Code);
        Assert.Contains("'datumTransformation'", exception.Message);
        Assert.Contains("WGS84_To_OSGB36_Helmert", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Project_honours_cancellation()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var parameters = await ParamsAsync(
            ("geometries", """[{"x":13.405,"y":52.52,"spatialReference":{"wkid":4326}}]"""),
            ("inSR", "4326"),
            ("outSR", "32632"));

        Assert.Throws<OperationCanceledException>(() =>
            GeometryService.Dispatch("project", parameters, Capabilities, cancelled.Token));
    }

    [Fact]
    public async Task Generalize_simplifies_with_the_deviation()
    {
        var result = await DispatchAsync("generalize",
            ("geometries", """[{"paths":[[[0,0],[1,0.1],[2,-0.1],[3,5],[4,6],[5,7],[6,8.1],[7,9],[8,9]]]}]"""),
            ("maxDeviation", "1"));

        Assert.True(result.GetProperty("geometries")[0].GetProperty("paths")[0].GetArrayLength() < 9);
    }

    [Fact]
    public async Task Generalize_requires_a_finite_deviation()
    {
        await Assert.ThrowsAsync<EsriInteropException>(() => DispatchAsync("generalize", ("geometries", "[]"), ("maxDeviation", "NaN")));
    }

    [Fact]
    public async Task Buffer_accepts_one_distance_for_all_geometries_and_quadrant_segments()
    {
        var result = await DispatchAsync("buffer",
            ("geometries", """[{"x":0,"y":0},{"x":10,"y":10}]"""),
            ("distances", "1"),
            ("quadrantSegments", "4"));

        Assert.Equal(2, result.GetProperty("geometries").GetArrayLength());
    }

    [Fact]
    public async Task Buffer_accepts_one_distance_per_geometry()
    {
        var result = await DispatchAsync("buffer",
            ("geometries", """[{"x":0,"y":0},{"x":10,"y":10}]"""),
            ("distances", "1,2"));

        Assert.Equal(2, result.GetProperty("geometries").GetArrayLength());
    }

    [Fact]
    public async Task Buffer_rejects_a_distance_count_mismatch()
    {
        await Assert.ThrowsAsync<EsriInteropException>(() => DispatchAsync("buffer",
            ("geometries", """[{"x":0,"y":0},{"x":10,"y":10}]"""),
            ("distances", "1,2,3")));
    }

    [Fact]
    public async Task Buffer_rejects_a_non_boolean_modifier()
    {
        await Assert.ThrowsAsync<EsriInteropException>(() => DispatchAsync("buffer",
            ("geometries", """[{"x":0,"y":0}]"""),
            ("distances", "1"),
            ("geodesic", "perhaps")));
    }

    [Fact]
    public async Task Buffer_accepts_unionResults_false_as_per_input()
    {
        var result = await DispatchAsync("buffer",
            ("geometries", """[{"x":0,"y":0}]"""),
            ("distances", "1"),
            ("unionResults", "false"));

        Assert.Equal(1, result.GetProperty("geometries").GetArrayLength());
    }

    [Fact]
    public async Task Buffer_serves_unionResults_true_as_one_dissolved_geometry()
    {
        // Two overlapping squares buffered and dissolved: the result array
        // holds a single geometry, and it is a valid one.
        var result = await DispatchAsync("buffer",
            ("geometries", """[{"x":0,"y":0},{"x":0.01,"y":0}]"""),
            ("inSR", "4326"),
            ("distances", "1"),
            ("unit", "9102"),
            ("unionResults", "true"));

        var geometries = result.GetProperty("geometries");
        Assert.Equal(1, geometries.GetArrayLength());
        var rings = geometries[0].GetProperty("rings");
        Assert.Equal(1, rings.GetArrayLength());

        // The dissolved pair spans both 1-degree circles and no more, which
        // is the whole claim: one geometry, not two, and not a bigger one.
        var xs = rings[0].EnumerateArray().Select(point => point[0].GetDouble()).ToArray();
        var ys = rings[0].EnumerateArray().Select(point => point[1].GetDouble()).ToArray();
        Assert.InRange(xs.Max() - xs.Min(), 2.005, 2.015);
        Assert.InRange(ys.Max() - ys.Min(), 1.995, 2.005);
    }

    [Fact]
    public async Task Buffer_rejects_unionResults_across_mixed_input_references()
    {
        var exception = await Assert.ThrowsAsync<EsriInteropException>(() => DispatchAsync("buffer",
            ("geometries", """[{"x":0,"y":0,"spatialReference":{"wkid":4326}},{"x":0,"y":0,"spatialReference":{"wkid":4258}}]"""),
            ("distances", "1"),
            ("unit", "9102"),
            ("unionResults", "true")));

        Assert.Contains("unionResults", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Buffer_with_unit_and_buffer_sr_buffers_in_the_projected_crs()
    {
        // distances=1000&unit=9001 (metres) against a 4326 point buffered in
        // 3857 must reproduce the projected result: a ~1000 m planar buffer,
        // not a 1000-degree planar buffer in the geographic CRS.
        var result = await DispatchAsync("buffer",
            ("geometries", """[{"x":0,"y":0,"spatialReference":{"wkid":4326}}]"""),
            ("inSR", "4326"),
            ("bufferSR", "3857"),
            ("outSR", "3857"),
            ("distances", "1000"),
            ("unit", "9001"));

        var ring = result.GetProperty("geometries")[0].GetProperty("rings")[0];
        var xs = ring.EnumerateArray().Select(point => point[0].GetDouble()).ToArray();
        var ys = ring.EnumerateArray().Select(point => point[1].GetDouble()).ToArray();
        Assert.True(xs.Min() < -900 && xs.Max() > 900);
        Assert.True(ys.Min() < -900 && ys.Max() > 900);
        Assert.True(xs.Max() - xs.Min() < 2100);
        Assert.True(ys.Max() - ys.Min() < 2100);
    }

    [Fact]
    public async Task Buffer_with_unit_matches_transform_then_buffer()
    {
        var result = await DispatchAsync("buffer",
            ("geometries", """[{"x":2.3522,"y":48.8566,"spatialReference":{"wkid":4326}}]"""),
            ("inSR", "4326"),
            ("bufferSR", "3857"),
            ("distances", "1000"),
            ("unit", "9001"));

        var ring = result.GetProperty("geometries")[0].GetProperty("rings")[0];
        var serviceXs = ring.EnumerateArray().Select(point => point[0].GetDouble()).ToArray();
        var serviceYs = ring.EnumerateArray().Select(point => point[1].GetDouble()).ToArray();

        var point = new Point(new Coordinate(2.3522, 48.8566), CoordinateReference.Epsg(4326));
        var projected = Transforms.Transform(point, null, "EPSG:3857", CancellationToken.None);
        var buffered = Operations.Buffer(projected, 1000, 8, CancellationToken.None);
        var expected = Transforms.Transform(buffered, null, "EPSG:3857", CancellationToken.None).Envelope!.Value;

        Assert.Equal(expected.MinX, serviceXs.Min(), 3);
        Assert.Equal(expected.MaxX, serviceXs.Max(), 3);
        Assert.Equal(expected.MinY, serviceYs.Min(), 3);
        Assert.Equal(expected.MaxY, serviceYs.Max(), 3);
    }

    [Fact]
    public async Task Buffer_without_out_sr_returns_the_buffer_crs()
    {
        var result = await DispatchAsync("buffer",
            ("geometries", """[{"x":0,"y":0,"spatialReference":{"wkid":4326}}]"""),
            ("inSR", "4326"),
            ("bufferSR", "3857"),
            ("distances", "1000"),
            ("unit", "9001"));

        Assert.Equal(3857, result.GetProperty("geometries")[0].GetProperty("spatialReference").GetProperty("wkid").GetInt32());
    }

    [Fact]
    public async Task Buffer_rejects_an_unknown_unit()
    {
        var exception = await Assert.ThrowsAsync<EsriInteropException>(() => DispatchAsync("buffer",
            ("geometries", """[{"x":0,"y":0}]"""),
            ("distances", "1"),
            ("unit", "424242")));

        Assert.Equal(EsriErrorCodes.InvalidParameters, exception.Code);
    }

    [Fact]
    public async Task Buffer_measures_a_symbolic_unit_name_like_its_code()
    {
        // The same two spellings the query's `units` takes: the shared
        // parse means the Geometry Service cannot drift from it
        // (ADR-0035 §4, ADR-0085).
        var named = await DispatchAsync("buffer",
            ("geometries", """[{"x":0,"y":0,"spatialReference":{"wkid":3857}}]"""),
            ("distances", "1000"),
            ("unit", "esriSRUnit_Meter"));
        var numeric = await DispatchAsync("buffer",
            ("geometries", """[{"x":0,"y":0,"spatialReference":{"wkid":3857}}]"""),
            ("distances", "1000"),
            ("unit", "9001"));

        var radius = named.GetProperty("geometries")[0].GetProperty("rings")[0];
        Assert.Equal(numeric.GetProperty("geometries")[0].GetProperty("rings")[0].ToString(), radius.ToString());
        Assert.Contains("1000", radius.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Buffer_names_the_two_spellings_for_an_unknown_unit()
    {
        var exception = await Assert.ThrowsAsync<EsriInteropException>(() => DispatchAsync("buffer",
            ("geometries", """[{"x":0,"y":0}]"""),
            ("distances", "1"),
            ("unit", "esriSRUnit_Furlong")));

        Assert.Equal(EsriErrorCodes.InvalidParameters, exception.Code);
        Assert.Contains("numeric esriSRUnitType code or an esriSRUnit_* name", exception.Message);
        Assert.Contains("esriSRUnit_Meter", exception.Message);
    }

    [Fact]
    public async Task Buffer_serves_a_linear_unit_against_a_geographic_crs()
    {
        // The shape of the request clients actually send: metres against a
        // 4326 geometry, with no projected bufferSR to think of. 1000 m at
        // the equator is about 0.009 degrees — a degree buffer would be
        // 111 km, so the two cannot be confused.
        var result = await DispatchAsync("buffer",
            ("geometries", """[{"x":0,"y":0,"spatialReference":{"wkid":4326}}]"""),
            ("inSR", "4326"),
            ("distances", "1000"),
            ("unit", "9001"));

        var ring = result.GetProperty("geometries")[0].GetProperty("rings")[0];
        var xs = ring.EnumerateArray().Select(point => point[0].GetDouble()).ToArray();
        var ys = ring.EnumerateArray().Select(point => point[1].GetDouble()).ToArray();
        Assert.Equal(4326, result.GetProperty("geometries")[0].GetProperty("spatialReference").GetProperty("wkid").GetInt32());
        Assert.InRange(xs.Max() - xs.Min(), 0.017, 0.019);
        Assert.InRange(ys.Max() - ys.Min(), 0.017, 0.019);
    }

    [Fact]
    public async Task Buffer_serves_a_linear_unit_against_a_geographic_buffer_sr()
    {
        // Naming the geographic CRS as the buffer CRS is the same request
        // said out loud; it must not fall back to a degree buffer.
        var result = await DispatchAsync("buffer",
            ("geometries", """[{"x":0,"y":0}]"""),
            ("bufferSR", "4326"),
            ("distances", "1000"),
            ("unit", "9001"));

        var ring = result.GetProperty("geometries")[0].GetProperty("rings")[0];
        var ys = ring.EnumerateArray().Select(point => point[1].GetDouble()).ToArray();
        Assert.InRange(ys.Max() - ys.Min(), 0.017, 0.019);
    }

    [Fact]
    public async Task Buffer_serves_geodesic_true_with_a_linear_unit()
    {
        var result = await DispatchAsync("buffer",
            ("geometries", """[{"x":0,"y":0,"spatialReference":{"wkid":4326}}]"""),
            ("inSR", "4326"),
            ("distances", "1000"),
            ("unit", "9001"),
            ("geodesic", "true"));

        var ring = result.GetProperty("geometries")[0].GetProperty("rings")[0];
        var ys = ring.EnumerateArray().Select(point => point[1].GetDouble()).ToArray();
        Assert.InRange(ys.Max() - ys.Min(), 0.017, 0.019);
    }

    [Fact]
    public async Task Buffer_returns_a_ground_distance_buffer_in_out_sr()
    {
        // The ground buffer happens in the geographic CRS and comes back
        // projected, so outSR still means outSR.
        var result = await DispatchAsync("buffer",
            ("geometries", """[{"x":0,"y":0,"spatialReference":{"wkid":4326}}]"""),
            ("inSR", "4326"),
            ("outSR", "3857"),
            ("distances", "1000"),
            ("unit", "9001"));

        var buffered = result.GetProperty("geometries")[0];
        Assert.Equal(3857, buffered.GetProperty("spatialReference").GetProperty("wkid").GetInt32());
        var ring = buffered.GetProperty("rings")[0];
        var xs = ring.EnumerateArray().Select(point => point[0].GetDouble()).ToArray();
        Assert.InRange(xs.Max() - xs.Min(), 1800.0, 2200.0);
    }

    [Fact]
    public async Task Buffer_erodes_through_a_negative_distance_in_metres()
    {
        // A 0.2-degree square (about 22 km across at the equator) eroded by
        // 5 km keeps a polygon whose edges have moved in by 5 km measured on
        // the ground, which is 0.045 degrees and not the same thing.
        var result = await DispatchAsync("buffer",
            ("geometries", """{"rings":[[[-0.1,-0.1],[0.1,-0.1],[0.1,0.1],[-0.1,0.1],[-0.1,-0.1]]],"spatialReference":{"wkid":4326}}"""),
            ("inSR", "4326"),
            ("distances", "-5000"),
            ("unit", "9001"));

        var ring = result.GetProperty("geometries")[0].GetProperty("rings")[0];
        var xs = ring.EnumerateArray().Select(point => point[0].GetDouble()).ToArray();
        var ys = ring.EnumerateArray().Select(point => point[1].GetDouble()).ToArray();
        Assert.InRange(xs.Max() - xs.Min(), 0.108, 0.112);
        Assert.InRange(ys.Max() - ys.Min(), 0.108, 0.112);
    }

    [Fact]
    public async Task Buffer_buffers_a_distance_per_geometry_in_metres()
    {
        var result = await DispatchAsync("buffer",
            ("geometries", """[{"x":0,"y":0},{"x":0,"y":0}]"""),
            ("inSR", "4326"),
            ("distances", "1000,2000"),
            ("unit", "9001"));

        var geometries = result.GetProperty("geometries");
        var first = geometries[0].GetProperty("rings")[0].EnumerateArray()
            .Select(point => point[1].GetDouble()).ToArray();
        var second = geometries[1].GetProperty("rings")[0].EnumerateArray()
            .Select(point => point[1].GetDouble()).ToArray();
        Assert.InRange(first.Max() - first.Min(), 0.017, 0.019);
        Assert.InRange(second.Max() - second.Min(), 0.035, 0.037);
    }

    [Fact]
    public async Task Buffer_refuses_a_ground_buffer_past_the_stated_tolerance()
    {
        // A small feature buffered by 500 km would not hold the stated 0.05%
        // tolerance, so the engine says why instead of answering wrongly.
        var exception = await Assert.ThrowsAsync<EsriInteropException>(() => DispatchAsync("buffer",
            ("geometries", """[{"x":0,"y":0,"spatialReference":{"wkid":4326}}]"""),
            ("inSR", "4326"),
            ("distances", "500000"),
            ("unit", "9001")));

        Assert.Equal(EsriErrorCodes.InvalidParameters, exception.Code);
        Assert.Contains("bufferSR", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Buffer_rejects_geodesic_true_with_an_angular_unit()
    {
        var exception = await Assert.ThrowsAsync<EsriInteropException>(() => DispatchAsync("buffer",
            ("geometries", """[{"x":0,"y":0,"spatialReference":{"wkid":4326}}]"""),
            ("inSR", "4326"),
            ("distances", "1"),
            ("unit", "9102"),
            ("geodesic", "true")));

        Assert.Equal(EsriErrorCodes.InvalidParameters, exception.Code);
        Assert.Contains("linear 'unit'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Buffer_rejects_geodesic_true_with_a_projected_buffer_sr()
    {
        var exception = await Assert.ThrowsAsync<EsriInteropException>(() => DispatchAsync("buffer",
            ("geometries", """[{"x":0,"y":0,"spatialReference":{"wkid":4326}}]"""),
            ("inSR", "4326"),
            ("bufferSR", "3857"),
            ("distances", "1000"),
            ("unit", "9001"),
            ("geodesic", "true")));

        Assert.Equal(EsriErrorCodes.InvalidParameters, exception.Code);
        Assert.Contains("projected", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Buffer_accepts_an_angular_unit_in_a_geographic_crs()
    {
        var result = await DispatchAsync("buffer",
            ("geometries", """[{"x":0,"y":0,"spatialReference":{"wkid":4326}}]"""),
            ("inSR", "4326"),
            ("distances", "1"),
            ("unit", "9102"));

        var ring = result.GetProperty("geometries")[0].GetProperty("rings")[0];
        var xs = ring.EnumerateArray().Select(point => point[0].GetDouble()).ToArray();
        Assert.True(xs.Min() < -0.9 && xs.Max() > 0.9);
    }

    [Fact]
    public async Task Buffer_accepts_geodesic_false_as_planar()
    {
        var result = await DispatchAsync("buffer",
            ("geometries", """[{"x":0,"y":0}]"""),
            ("distances", "1"),
            ("geodesic", "false"));

        Assert.Equal(1, result.GetProperty("geometries").GetArrayLength());
    }

    [Fact]
    public async Task Buffer_honours_cancellation_on_the_projected_path()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var parameters = await ParamsAsync(
            ("geometries", """[{"x":0,"y":0,"spatialReference":{"wkid":4326}}]"""),
            ("inSR", "4326"),
            ("bufferSR", "3857"),
            ("distances", "1000"),
            ("unit", "9001"));

        Assert.Throws<OperationCanceledException>(() =>
            GeometryService.Dispatch("buffer", parameters, Capabilities, cancelled.Token));
    }

    [Fact]
    public async Task Find_transformations_returns_empty_for_the_same_datum()
    {
        var result = await DispatchAsync("findTransformations",
            ("inSR", "4326"),
            ("outSR", "3857"));

        Assert.Equal(0, result.GetArrayLength());
    }

    [Fact]
    public async Task Find_transformations_lists_ranked_candidates_with_their_parameters()
    {
        // 4326 (WGS 84) to 27700 (OSGB36): the engine applies the composed
        // geocentric shift, so the listing leads with it, carries the
        // catalogue's Helmert parameters and states the accuracy.
        var result = await DispatchAsync("findTransformations",
            ("inSR", "4326"),
            ("outSR", "27700"));

        Assert.True(result.GetArrayLength() >= 2, "a datum step must offer more than one operation.");
        var applied = result[0];
        Assert.Equal("WGS84_To_OSGB36_Helmert", applied.GetProperty("name").GetString());
        var step = applied.GetProperty("geoTransforms")[0];
        Assert.True(step.GetProperty("transformForward").GetBoolean());
        Assert.Contains("Helmert", step.GetProperty("name").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Helmert", step.GetProperty("method").GetString(), StringComparison.OrdinalIgnoreCase);
        // The accuracy EPSG publishes for the operation the engine applies,
        // EPSG:1314 "OSGB36 to WGS 84 (6)": 2.0 m. It was 3.0 here while the
        // table carried a figure traceable to no registered operation
        // (SpatialEngine-u2x.28.1).
        Assert.Equal(2.0, applied.GetProperty("accuracy").GetDouble(), 3);
        Assert.False(applied.GetProperty("approximate").GetBoolean());
        var helmert = step.GetProperty("helmert");
        Assert.Equal(20.489, helmert.GetProperty("scale").GetDouble(), 3);
        Assert.Equal(-542.072, helmert.GetProperty("tz").GetDouble(), 3);
        // The area of use is a list of rectangles (ADR-0111). It is one here,
        // because no extent in the catalogue crosses the antimeridian, and a
        // client reading a single rectangle out of a one-element list reads
        // the envelope the spec sketches.
        var areaOfUse = Assert.Single(applied.GetProperty("areaOfUse").EnumerateArray());
        Assert.Equal(49.79, areaOfUse.GetProperty("ymin").GetDouble(), 3);
        Assert.Contains("Great Britain", areaOfUse.GetProperty("name").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_extent_of_interest_across_the_antimeridian_is_not_sorted_into_one_envelope()
    {
        // A client asking about ground either side of the seam sends a west
        // of 170E and an east of 172W. Ordering the two into one interval
        // would make the request the whole Pacific, which contains Great
        // Britain, and the direct operation would come back for a question
        // about New Zealand. Split at the antimeridian, neither half is in
        // Britain and only the concatenated path — valid wherever either step
        // applies, and WGS 84 applies everywhere — survives (ADR-0111).
        var overNewZealand = await DispatchAsync(
            "findTransformations",
            ("inSR", "4326"),
            ("outSR", "27700"),
            ("extentOfInterest", "170,-45,-172,-42"));

        var concatenated = Assert.Single(overNewZealand.EnumerateArray());
        Assert.Equal("WGS84_To_OSGB36_Helmert_via_WGS84", concatenated.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Find_transformations_is_symmetric()
    {
        var forward = await DispatchAsync("findTransformations", ("inSR", "4326"), ("outSR", "27700"));
        var reverse = await DispatchAsync("findTransformations", ("inSR", "27700"), ("outSR", "4326"));

        Assert.Equal(forward.GetArrayLength(), reverse.GetArrayLength());
        for (var index = 0; index < forward.GetArrayLength(); index++)
        {
            Assert.Equal(
                forward[index].GetProperty("name").GetString(),
                reverse[index].GetProperty("name").GetString());
            var steps = reverse[index].GetProperty("geoTransforms");
            Assert.All(steps.EnumerateArray(), step => Assert.False(step.GetProperty("transformForward").GetBoolean()));
        }
    }

    [Fact]
    public async Task Find_transformations_filters_by_extent_of_interest()
    {
        // Great Britain: every candidate applies. France: only the path
        // through the world datum's own domain does.
        var inBritain = await DispatchAsync("findTransformations",
            ("inSR", "4326"),
            ("outSR", "27700"),
            ("extentOfInterest", "-2,51.5,0,53"));
        var inFrance = await DispatchAsync("findTransformations",
            ("inSR", "4326"),
            ("outSR", "27700"),
            ("extentOfInterest", """{"xmin":2,"ymin":45,"xmax":6,"ymax":48}"""));

        Assert.True(inBritain.GetArrayLength() > 1, "an area of interest must not empty a search over Great Britain.");
        Assert.Equal(1, inFrance.GetArrayLength());
        Assert.Contains("via_WGS84", inFrance[0].GetProperty("name").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Find_transformations_interprets_the_extent_in_the_source_crs()
    {
        // A projected inSR means the extent arrives in metres; the search
        // still filters on the geographic box the catalogue records.
        var result = await DispatchAsync("findTransformations",
            ("inSR", "27700"),
            ("outSR", "4326"),
            ("extentOfInterest", "500000,170000,540000,190000"));

        Assert.True(result.GetArrayLength() > 1, "a British extent over the projected source CRS must keep the local candidates.");
    }

    [Fact]
    public async Task Find_transformations_honours_the_horizontal_default()
    {
        var horizontal = await DispatchAsync("findTransformations",
            ("inSR", "4326"),
            ("outSR", "27700"),
            ("vertical", "false"));
        var plain = await DispatchAsync("findTransformations", ("inSR", "4326"), ("outSR", "27700"));

        Assert.Equal(plain.GetArrayLength(), horizontal.GetArrayLength());
    }

    [Fact]
    public async Task Find_transformations_still_refuses_a_vertical_search()
    {
        var exception = await Assert.ThrowsAsync<EsriInteropException>(() => DispatchAsync("findTransformations",
            ("inSR", "4326"),
            ("outSR", "27700"),
            ("vertical", "true")));

        Assert.Contains("'vertical'", exception.Message);
    }

    [Fact]
    public async Task Find_transformations_requires_both_references()
    {
        await Assert.ThrowsAsync<EsriInteropException>(() => DispatchAsync("findTransformations", ("inSR", "4326")));
    }

    [Fact]
    public async Task Find_transformations_rejects_unknown_references()
    {
        await Assert.ThrowsAsync<EsriInteropException>(() => DispatchAsync("findTransformations",
            ("inSR", "4326"),
            ("outSR", "4267")));
    }

    [Theory]
    [InlineData("extentOfInterest", "not-an-extent")]
    [InlineData("extentOfInterest", "1,2,3")]
    [InlineData("numOfResults", "many")]
    public async Task Find_transformations_rejects_a_malformed_modifier(string name, string value)
    {
        await Assert.ThrowsAsync<EsriInteropException>(() => DispatchAsync("findTransformations",
            ("inSR", "4326"),
            ("outSR", "27700"),
            (name, value)));
    }

    [Fact]
    public async Task Find_transformations_honours_the_result_count()
    {
        var none = await DispatchAsync("findTransformations",
            ("inSR", "4326"),
            ("outSR", "27700"),
            ("numOfResults", "0"));
        Assert.Equal(0, none.GetArrayLength());

        var all = await DispatchAsync("findTransformations",
            ("inSR", "4326"),
            ("outSR", "27700"),
            ("numOfResults", "-1"));
        var every = await DispatchAsync("findTransformations", ("inSR", "4326"), ("outSR", "27700"));
        Assert.Equal(every.GetArrayLength(), all.GetArrayLength());

        // 'numTransformations' is the same argument under the name the
        // ArcGIS REST JS client uses.
        var aliased = await DispatchAsync("findTransformations",
            ("inSR", "4326"),
            ("outSR", "27700"),
            ("numTransformations", "1"));
        Assert.Equal(1, aliased.GetArrayLength());
    }

    [Fact]
    public async Task Find_transformations_honours_cancellation()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var parameters = await ParamsAsync(("inSR", "4326"), ("outSR", "27700"));

        Assert.Throws<OperationCanceledException>(() =>
            GeometryService.Dispatch("findTransformations", parameters, Capabilities, cancelled.Token));
    }

    [Theory]
    [InlineData("fromGeoCoordinateString")]
    [InlineData("toGeoCoordinateString")]
    public async Task Coordinate_notation_operations_are_honest_non_goals(string operation)
    {
        // No MGRS/USNG/UTM/GeoRef/GARS/DMS/DDM/DD codec in the tree and no
        // engine verb: reject by name rather than half-parse notations.
        var exception = await Assert.ThrowsAsync<EsriInteropException>(() => DispatchAsync(operation,
            ("conversionType", "MGRS")));

        Assert.Equal(EsriErrorCodes.InvalidParameters, exception.Code);
    }

    [Fact]
    public async Task Intersect_returns_the_overlap()
    {
        var result = await DispatchAsync("intersect",
            ("geometries", """[{"xmin":-1,"ymin":-1,"xmax":1,"ymax":1,"spatialReference":{"wkid":4326}}]"""),
            ("geometry", """{"xmin":0,"ymin":0,"xmax":2,"ymax":2,"spatialReference":{"wkid":4326}}"""));

        Assert.Equal(1, result.GetProperty("geometries").GetArrayLength());
    }

    [Fact]
    public async Task Areas_and_lengths_returns_both_arrays()
    {
        var result = await DispatchAsync("areasandlengths",
            ("geometries", """[{"rings":[[[0,0],[1,0],[1,1],[0,1],[0,0]]]}]"""));

        Assert.Equal(1.0, result.GetProperty("areas")[0].GetDouble());
        Assert.True(result.GetProperty("lengths")[0].GetDouble() > 0);
    }

    [Fact]
    public async Task Lengths_returns_one_value_per_geometry()
    {
        var result = await DispatchAsync("lengths",
            ("geometries", """[{"paths":[[[0,0],[3,4]]]}]"""));

        Assert.Equal(5.0, result.GetProperty("lengths")[0].GetDouble());
    }

    [Fact]
    public async Task Areas_and_lengths_accepts_docs_polygons_alias_verbatim()
    {
        // Esri-docs verbatim (T-064 fixtures): areasAndLengths names the
        // input 'polygons' with sr + calculationType=planar on the wire.
        var result = await DispatchAsync("areasandlengths",
            ("polygons", """[{"rings":[[[0,0],[0,1],[1,1],[1,0],[0,0]]]}]"""),
            ("sr", "4326"),
            ("calculationType", "planar"));

        Assert.Equal(1.0, result.GetProperty("areas")[0].GetDouble());
        Assert.Equal(4.0, result.GetProperty("lengths")[0].GetDouble());
    }

    [Fact]
    public async Task Areas_and_lengths_accepts_polys_alias()
    {
        var result = await DispatchAsync("areasandlengths",
            ("polys", """[{"rings":[[[0,0],[0,1],[1,1],[1,0],[0,0]]]}]"""),
            ("sr", "4326"));

        Assert.Equal(1.0, result.GetProperty("areas")[0].GetDouble());
        Assert.Equal(4.0, result.GetProperty("lengths")[0].GetDouble());
    }

    [Fact]
    public async Task Lengths_accepts_docs_polylines_alias_verbatim()
    {
        // Esri-docs verbatim (T-064 fixtures): lengths names the input
        // 'polylines' with sr + calculationType=planar on the wire.
        var result = await DispatchAsync("lengths",
            ("polylines", """[{"paths":[[[0,0],[3,4]]]}]"""),
            ("sr", "4326"),
            ("calculationType", "planar"));

        Assert.Equal(5.0, result.GetProperty("lengths")[0].GetDouble());
    }

    [Fact]
    public async Task Areas_and_lengths_prefers_geometries_when_both_names_are_present()
    {
        var result = await DispatchAsync("areasandlengths",
            ("geometries", """[{"rings":[[[0,0],[1,0],[1,1],[0,1],[0,0]]]}]"""),
            ("polygons", """[{"rings":[[[0,0],[2,0],[2,2],[0,2],[0,0]]]}]"""));

        Assert.Equal(1.0, result.GetProperty("areas")[0].GetDouble());
    }

    [Theory]
    [InlineData("areasandlengths", "polygons", "[{\"rings\":[[[0,0],[0,1],[1,1],[1,0],[0,0]]]}]")]
    [InlineData("lengths", "polylines", "[{\"paths\":[[[0,0],[3,4]]]}]")]
    public async Task Docs_alias_operations_reject_non_planar_calculation_honestly(string operation, string alias, string payload)
    {
        // The engine measures planar (no geodesic verb): a non-planar
        // calculationType must fail honestly rather than answer planar
        // silently.
        var exception = await Assert.ThrowsAsync<EsriInteropException>(() => DispatchAsync(operation,
            (alias, payload),
            ("calculationType", "geodesic")));

        Assert.Equal(EsriErrorCodes.InvalidParameters, exception.Code);
        Assert.Contains("calculationType", exception.Message);
    }

    [Fact]
    public async Task Distance_returns_the_planar_distance()
    {
        var result = await DispatchAsync("distance",
            ("geometry1", """{"x":0,"y":0}"""),
            ("geometry2", """{"x":3,"y":4}"""));

        Assert.Equal(5.0, result.GetProperty("distance").GetDouble());
    }

    [Fact]
    public async Task Convex_hull_wraps_the_inputs()
    {
        var result = await DispatchAsync("convexhull",
            ("geometries", """[{"x":0,"y":0},{"x":2,"y":0},{"x":1,"y":2}]"""));

        Assert.Equal(1, result.GetProperty("geometries").GetArrayLength());
    }

    [Fact]
    public async Task Difference_subtracts_the_second_geometry()
    {
        var result = await DispatchAsync("difference",
            ("geometries", """[{"rings":[[[0,0],[4,0],[4,4],[0,4],[0,0]]]}]"""),
            ("geometry", """{"rings":[[[2,0],[6,0],[6,4],[2,4],[2,0]]]}"""));

        Assert.Equal(1, result.GetProperty("geometries").GetArrayLength());
    }

    [Fact]
    public async Task Union_merges_the_inputs()
    {
        var result = await DispatchAsync("union",
            ("geometries", """[{"rings":[[[0,0],[2,0],[2,2],[0,2],[0,0]]]},{"rings":[[[2,0],[4,0],[4,2],[2,2],[2,0]]]}]"""));

        Assert.Equal(1, result.GetProperty("geometries").GetArrayLength());
    }

    [Fact]
    public async Task Relation_returns_one_flag_per_geometry()
    {
        var result = await DispatchAsync("relation",
            ("geometries", """[{"xmin":0,"ymin":0,"xmax":2,"ymax":2},{"xmin":10,"ymin":10,"xmax":11,"ymax":11}]"""),
            ("geometry", """{"xmin":0,"ymin":0,"xmax":1,"ymax":1}"""),
            ("relationParam", "T*****FF*"));

        var relations = result.GetProperty("relations").EnumerateArray().Select(value => value.GetInt32()).ToArray();
        Assert.Equal(1, relations[0]);
        Assert.Equal(0, relations[1]);
    }

    [Fact]
    public async Task Densify_subdivides_long_segments()
    {
        var result = await DispatchAsync("densify",
            ("geometries", """[{"paths":[[[0,0],[10,0]]]}]"""),
            ("maxSegmentLength", "2"));

        Assert.True(result.GetProperty("geometries")[0].GetProperty("paths")[0].GetArrayLength() > 2);
    }

    [Fact]
    public async Task Label_points_returns_an_interior_point()
    {
        var result = await DispatchAsync("labelpoints",
            ("geometries", """[{"rings":[[[0,0],[2,0],[2,2],[0,2],[0,0]]]}]"""));

        Assert.Single(result.GetProperty("geometries").EnumerateArray());
    }
}
