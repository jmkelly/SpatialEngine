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
        await Assert.ThrowsAsync<EsriInteropException>(() => ParseAsync(("spatialRel", "esriSpatialRelBogus")));
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
    [InlineData("returnZ")]
    [InlineData("returnM")]
    [InlineData("sqlFormat")]
    [InlineData("resultType")]
    [InlineData("gdbVersion")]
    [InlineData("historicMoment")]
    [InlineData("datumTransformation")]
    [InlineData("returnCentroid")]
    [InlineData("distance")]
    [InlineData("units")]
    [InlineData("relationParam")]
    [InlineData("text")]
    [InlineData("returnTrueCurves")]
    [InlineData("multipatchOption")]
    [InlineData("mosaicRule")]
    [InlineData("renderingRule")]
    [InlineData("bandIds")]
    public async Task Unsupported_query_parameters_are_rejected(string name)
    {
        var exception = await Assert.ThrowsAsync<EsriInteropException>(() => ParseAsync((name, "x")));

        Assert.Contains($"'{name}'", exception.Message);
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

    [Fact]
    public async Task Out_statistics_group_by_and_having_are_parsed()
    {
        var query = await ParseAsync(
            ("outStatistics", "[{\"statisticType\":\"sum\",\"onStatisticField\":\"population\",\"outStatisticFieldName\":\"sumpop\"}]"),
            ("groupByFieldsForStatistics", "name"),
            ("having", "sumpop > 100"));

        Assert.Single(query.OutStatistics!);
        Assert.Equal("sum", query.OutStatistics![0].StatisticType);
        Assert.Equal(["name"], query.GroupByFields);
        Assert.NotNull(query.Having);
    }

    [Fact]
    public async Task Group_by_without_statistics_is_rejected()
    {
        await Assert.ThrowsAsync<EsriInteropException>(() => ParseAsync(("groupByFieldsForStatistics", "name")));
    }

    [Fact]
    public async Task Statistics_conflict_with_other_result_shapes()
    {
        const string stats = "[{\"statisticType\":\"count\",\"onStatisticField\":\"*\",\"outStatisticFieldName\":\"n\"}]";
        await Assert.ThrowsAsync<EsriInteropException>(() => ParseAsync(("outStatistics", stats), ("returnCountOnly", "true")));
    }

    [Fact]
    public async Task In_sr_is_honoured_for_a_geometry_without_its_own_reference()
    {
        var query = EsriFeatureQuery.Parse(
            await ParamsAsync(("geometry", "13.405,52.52"), ("inSR", "3857")),
            CoordinateReference.Epsg(4326));

        Assert.Equal(CoordinateReference.Epsg(3857), query.Geometry!.CoordinateReference);
    }

    [Fact]
    public async Task Paging_limit_parameters_are_parsed()
    {
        var query = await ParseAsync(("returnExceededLimitFeatures", "true"), ("maxRecordCountFactor", "3"));

        Assert.True(query.ReturnExceededLimitFeatures);
        Assert.Equal(3, query.MaxRecordCountFactor);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("abc")]
    public async Task Bad_max_record_count_factors_are_rejected(string value)
    {
        await Assert.ThrowsAsync<EsriInteropException>(() => ParseAsync(("maxRecordCountFactor", value)));
    }

    [Theory]
    [InlineData("esriSpatialRelContains")]
    [InlineData("esriSpatialRelWithin")]
    [InlineData("esriSpatialRelTouches")]
    [InlineData("esriSpatialRelOverlaps")]
    [InlineData("esriSpatialRelCrosses")]
    public async Task Remaining_spatial_relations_are_accepted(string spatialRel)
    {
        var query = await ParseAsync(("spatialRel", spatialRel));

        Assert.Equal(spatialRel, query.SpatialRel);
    }

    [Fact]
    public async Task Index_intersects_stays_rejected()
    {
        await Assert.ThrowsAsync<EsriInteropException>(() => ParseAsync(("spatialRel", "esriSpatialRelIndexIntersects")));
    }

    [Fact]
    public async Task Quantization_is_rejected_while_precision_and_offset_parse()
    {
        await Assert.ThrowsAsync<EsriInteropException>(() => ParseAsync(("quantizationParameters", "{\"mode\":\"view\"}")));
        var query = await ParseAsync(("geometryPrecision", "2"), ("maxAllowableOffset", "10"));
        Assert.Equal(2, query.GeometryPrecision);
        Assert.Equal(10.0, query.MaxAllowableOffset);
    }

    [Fact]
    public async Task A_time_instant_parses_to_a_point_extent()
    {
        var query = await ParseAsync(("time", "1199145600000"));

        Assert.NotNull(query.Time);
        Assert.Equal(1199145600000L, query.Time.StartMs);
        Assert.Equal(1199145600000L, query.Time.EndMs);
    }

    [Fact]
    public async Task A_time_extent_parses_with_null_infinity_bounds()
    {
        var openStart = await ParseAsync(("time", "null,1199145600000"));
        Assert.Null(openStart.Time!.StartMs);
        Assert.Equal(1199145600000L, openStart.Time.EndMs);

        var openEnd = await ParseAsync(("time", "1199145600000,null"));
        Assert.Equal(1199145600000L, openEnd.Time!.StartMs);
        Assert.Null(openEnd.Time.EndMs);

        var closed = await ParseAsync(("time", "1199145600000,1230768000000"));
        Assert.Equal(1199145600000L, closed.Time!.StartMs);
        Assert.Equal(1230768000000L, closed.Time.EndMs);
    }

    [Theory]
    [InlineData("yesterday")]
    [InlineData("1,2,3")]
    [InlineData("null")]
    public async Task A_malformed_time_is_rejected(string value)
    {
        await Assert.ThrowsAsync<EsriInteropException>(() => ParseAsync(("time", value)));
    }

    [Fact]
    public async Task No_time_leaves_the_extent_absent()
    {
        Assert.Null((await ParseAsync()).Time);
    }
}
