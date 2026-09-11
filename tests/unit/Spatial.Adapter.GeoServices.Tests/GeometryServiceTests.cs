using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Spatial.Interop.Esri;
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
    private static readonly GeometryServiceCapabilities Capabilities = new(
        new NtsGeometryOperations(),
        new NtsGeometryMeasures(),
        new NtsGeometryProcessing(),
        new NtsGeometryRelations(),
        new ProjNetTransforms());

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
        Assert.Contains("AreasAndLengths", info.GetProperty("capabilities").GetString());
    }

    [Fact]
    public async Task An_unknown_operation_is_rejected()
    {
        await Assert.ThrowsAsync<EsriInteropException>(() => DispatchAsync("rotate"));
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
    [InlineData("unit")]
    [InlineData("geodesic")]
    [InlineData("unionResults")]
    public async Task Buffer_rejects_unsupported_modifiers(string name)
    {
        await Assert.ThrowsAsync<EsriInteropException>(() => DispatchAsync("buffer",
            ("geometries", """[{"x":0,"y":0}]"""),
            ("distances", "1"),
            (name, "x")));
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
