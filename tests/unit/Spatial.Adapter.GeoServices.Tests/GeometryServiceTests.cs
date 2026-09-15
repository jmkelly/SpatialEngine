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
/// the shaped result. The semantic trap is pinned: <c>generalize</c> is
/// simplification, <c>simplify</c> is topological repair.
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
        Transforms);

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
    public async Task Project_requires_out_sr()
    {
        await Assert.ThrowsAsync<EsriInteropException>(() => DispatchAsync("project", ("geometries", "[]")));
    }

    [Fact]
    public async Task Project_rejects_datum_transformation()
    {
        // The engine has no datum tables: a client-supplied transformation
        // must fail honestly rather than project silently without it.
        var exception = await Assert.ThrowsAsync<EsriInteropException>(() => DispatchAsync("project",
            ("geometries", """[{"x":13.405,"y":52.52,"spatialReference":{"wkid":4326}}]"""),
            ("inSR", "4326"),
            ("outSR", "32632"),
            ("datumTransformation", "1")));

        Assert.Equal(EsriErrorCodes.InvalidParameters, exception.Code);
        Assert.Contains("'datumTransformation'", exception.Message);
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

    [Theory]
    [InlineData("geodesic")]
    public async Task Buffer_rejects_unsupported_modifiers(string name)
    {
        await Assert.ThrowsAsync<EsriInteropException>(() => DispatchAsync("buffer",
            ("geometries", """[{"x":0,"y":0}]"""),
            ("distances", "1"),
            (name, "x")));
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
    public async Task Buffer_rejects_unionResults_true_by_name()
    {
        var exception = await Assert.ThrowsAsync<EsriInteropException>(() => DispatchAsync("buffer",
            ("geometries", """[{"x":0,"y":0}]"""),
            ("distances", "1"),
            ("unionResults", "true")));

        Assert.Contains("unionResults", exception.Message);
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
    public async Task Buffer_rejects_a_linear_unit_in_a_geographic_buffer_crs()
    {
        // The planar engine cannot buffer metres in degrees (no geodesic
        // verb): the caller must name a projected bufferSR.
        await Assert.ThrowsAsync<EsriInteropException>(() => DispatchAsync("buffer",
            ("geometries", """[{"x":0,"y":0,"spatialReference":{"wkid":4326}}]"""),
            ("inSR", "4326"),
            ("distances", "1000"),
            ("unit", "9001")));
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
    public async Task Find_transformations_lists_the_curated_catalogue_path()
    {
        // 4326 (WGS 84) to 27700 (OSGB36): the engine applies the embedded
        // Helmert shift, so the listing must name the classic Helmert path.
        var result = await DispatchAsync("findTransformations",
            ("inSR", "4326"),
            ("outSR", "27700"));

        Assert.Equal(1, result.GetArrayLength());
        var steps = result[0].GetProperty("geoTransforms");
        Assert.Equal(1, steps.GetArrayLength());
        Assert.True(steps[0].GetProperty("transformForward").GetBoolean());
        Assert.Contains("Helmert", steps[0].GetProperty("name").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Helmert", steps[0].GetProperty("method").GetString(), StringComparison.OrdinalIgnoreCase);
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
    [InlineData("vertical", "true")]
    [InlineData("extentOfInterest", "{\"xmin\":0,\"ymin\":0,\"xmax\":1,\"ymax\":1}")]
    public async Task Find_transformations_rejects_unsupported_ranking(string name, string value)
    {
        await Assert.ThrowsAsync<EsriInteropException>(() => DispatchAsync("findTransformations",
            ("inSR", "4326"),
            ("outSR", "27700"),
            (name, value)));
    }

    [Fact]
    public async Task Find_transformations_honours_num_of_results()
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
        Assert.Equal(1, all.GetArrayLength());
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
    public async Task Simplify_repairs_a_self_intersecting_ring()
    {
        var result = await DispatchAsync("simplify",
            ("geometries", """[{"rings":[[[0,0],[2,2],[2,0],[0,2],[0,0]]]}]"""));

        Assert.Equal(2, result.GetProperty("geometries")[0].GetProperty("rings").GetArrayLength());
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
