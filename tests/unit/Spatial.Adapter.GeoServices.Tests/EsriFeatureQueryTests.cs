using Microsoft.AspNetCore.Http;
using Spatial.Core.Geometry;
using Spatial.Interop.Esri;

namespace Spatial.Adapter.GeoServices.Tests;

/// <summary>
/// The Feature Service <c>query</c> request parser (spec §9.1.4): the
/// supported v1.0 subset is enforced and the documented 10.x additions are
/// rejected explicitly rather than silently widened.
/// </summary>
public sealed class EsriFeatureQueryTests
{
    private static async Task<EsriRequestParameters> ParamsAsync(params (string Key, string Value)[] values)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = QueryString.Create(values.Select(value => new KeyValuePair<string, string?>(value.Key, value.Value)));
        return await EsriRequestParameters.ReadAsync(context, CancellationToken.None);
    }

    private static async Task<EsriFeatureQuery> ParseAsync(params (string Key, string Value)[] values) =>
        EsriFeatureQuery.Parse(await ParamsAsync(values), fallback: null);

    [Fact]
    public async Task Defaults_are_the_coarse_envelope_relation_and_geometry_output()
    {
        var query = await ParseAsync();

        Assert.Null(query.ObjectIds);
        Assert.Null(query.Where);
        Assert.Null(query.Geometry);
        Assert.Equal(EsriFeatureQuery.EnvelopeIntersects, query.SpatialRel);
        Assert.Null(query.OutFields);
        Assert.Null(query.OrderByFields);
        Assert.True(query.ReturnGeometry);
        Assert.Null(query.OutSr);
        Assert.False(query.ReturnIdsOnly);
        Assert.False(query.ReturnCountOnly);
        Assert.False(query.ReturnExtentOnly);
        Assert.False(query.ReturnDistinctValues);
        Assert.Null(query.ResultOffset);
        Assert.Null(query.ResultRecordCount);
    }

    [Fact]
    public async Task Object_ids_where_and_geometry_are_parsed()
    {
        var query = await ParseAsync(
            ("objectIds", "1,2"),
            ("where", "name = 'x'"),
            ("geometry", "{\"x\":1,\"y\":2}"),
            ("returnGeometry", "false"));

        Assert.Equal([1L, 2L], query.ObjectIds);
        Assert.NotNull(query.Where);
        Assert.NotNull(query.Geometry);
        Assert.False(query.ReturnGeometry);
    }

    [Fact]
    public async Task Geometry_uses_the_fallback_spatial_reference()
    {
        var query = EsriFeatureQuery.Parse(await ParamsAsync(("geometry", "{\"x\":1,\"y\":2}")), CoordinateReference.Epsg(4326));

        Assert.Equal(CoordinateReference.Epsg(4326), query.Geometry!.CoordinateReference);
    }

    [Fact]
    public async Task The_exact_intersection_relation_is_accepted()
    {
        var query = await ParseAsync(("spatialRel", EsriFeatureQuery.Intersects));

        Assert.Equal(EsriFeatureQuery.Intersects, query.SpatialRel);
    }

    [Fact]
    public async Task An_unsupported_relation_is_rejected()
    {
        await Assert.ThrowsAsync<EsriInteropException>(() => ParseAsync(("spatialRel", "esriSpatialRelContains")));
    }

    [Fact]
    public async Task Out_fields_accepts_either_star_or_an_explicit_list()
    {
        Assert.Null((await ParseAsync(("outFields", "*"))).OutFields);
        Assert.Null((await ParseAsync(("outFields", "  "))).OutFields);
        Assert.Equal(["name", "population"], (await ParseAsync(("outFields", "name, population"))).OutFields);
    }

    [Fact]
    public async Task Order_by_fields_parses_directions()
    {
        var query = await ParseAsync(("orderByFields", "name,population DESC"));

        Assert.Equal([new EsriOrderByField("name", false), new EsriOrderByField("population", true)], query.OrderByFields);
    }

    [Theory]
    [InlineData("name UP")]
    [InlineData("name ASC EXTRA")]
    public async Task Malformed_order_by_entries_are_rejected(string value)
    {
        await Assert.ThrowsAsync<EsriInteropException>(() => ParseAsync(("orderByFields", value)));
    }

    [Theory]
    [InlineData("resultOffset", "-1")]
    [InlineData("resultRecordCount", "abc")]
    public async Task Bad_numeric_paging_is_rejected(string name, string value)
    {
        await Assert.ThrowsAsync<EsriInteropException>(() => ParseAsync((name, value)));
    }

    [Fact]
    public async Task Paging_values_are_parsed()
    {
        var query = await ParseAsync(("resultOffset", "5"), ("resultRecordCount", "10"));

        Assert.Equal(5, query.ResultOffset);
        Assert.Equal(10, query.ResultRecordCount);
    }

    [Fact]
    public async Task Result_shapes_are_mutually_exclusive()
    {
        await Assert.ThrowsAsync<EsriInteropException>(() => ParseAsync(("returnIdsOnly", "true"), ("returnCountOnly", "true")));
    }

    [Fact]
    public async Task A_single_result_shape_is_accepted()
    {
        Assert.True((await ParseAsync(("returnIdsOnly", "true"))).ReturnIdsOnly);
        Assert.True((await ParseAsync(("returnCountOnly", "true"))).ReturnCountOnly);
        Assert.True((await ParseAsync(("returnExtentOnly", "true"))).ReturnExtentOnly);
        Assert.True((await ParseAsync(("returnDistinctValues", "true"))).ReturnDistinctValues);
    }

    [Theory]
    [InlineData("outStatistics")]
    [InlineData("groupByFieldsForStatistics")]
    [InlineData("returnZ")]
    [InlineData("returnM")]
    public async Task Unsupported_query_parameters_are_rejected(string name)
    {
        await Assert.ThrowsAsync<EsriInteropException>(() => ParseAsync((name, "x")));
    }

    [Fact]
    public async Task A_malformed_where_clause_is_rejected()
    {
        await Assert.ThrowsAsync<EsriInteropException>(() => ParseAsync(("where", "name = ")));
    }

    [Fact]
    public async Task A_malformed_geometry_is_rejected()
    {
        await Assert.ThrowsAsync<EsriInteropException>(() => ParseAsync(("geometry", "nope")));
    }

    [Fact]
    public async Task A_malformed_object_id_list_is_rejected()
    {
        await Assert.ThrowsAsync<EsriInteropException>(() => ParseAsync(("objectIds", "1,x")));
    }

    [Fact]
    public async Task Out_sr_is_parsed()
    {
        var query = await ParseAsync(("outSR", "4326"));

        Assert.Equal(CoordinateReference.Epsg(4326), query.OutSr);
    }
}
