using Microsoft.AspNetCore.Http;
using Spatial.Interop.Esri;

namespace Spatial.Adapter.GeoServices.Tests;

public sealed class QualityLoopPass4Tests
{
    [Theory]
    [InlineData("9001", 9001)]
    [InlineData(" 9102 ", 9102)]
    [InlineData("109001", 109001)]
    public void ParseUnitCode_accepts_curated_codes(string raw, int expected) =>
        Assert.Equal(expected, GeometryService.ParseUnitCode(raw));

    [Theory]
    [InlineData("metres")]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseUnitCode_rejects_non_numeric_codes(string raw) =>
        Assert.Throws<EsriInteropException>(() => GeometryService.ParseUnitCode(raw));

    [Fact]
    public void ParseUnitCode_rejects_codes_outside_the_curated_table() =>
        Assert.Throws<EsriInteropException>(() => GeometryService.ParseUnitCode("999999"));

    private static async Task<EsriRequestParameters> ParamsAsync(params (string Key, string Value)[] values)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = QueryString.Create(values.Select(value => new KeyValuePair<string, string?>(value.Key, value.Value)));
        return await EsriRequestParameters.ReadAsync(context, CancellationToken.None);
    }

    private static async Task<EsriFeatureQuery> ParseAsync(params (string Key, string Value)[] values) =>
        EsriFeatureQuery.Parse(await ParamsAsync(values), fallback: null);

    [Fact]
    public async Task RejectLayerOnlyShapes_accepts_plain_layer_queries()
    {
        FeatureServiceQuery.RejectLayerOnlyShapes(await ParseAsync(("where", "1=1")));
        FeatureServiceQuery.RejectLayerOnlyShapes(await ParseAsync());
    }

    [Theory]
    [InlineData("returnExtentOnly", "true")]
    [InlineData("returnDistinctValues", "true")]
    [InlineData("returnUniqueIdsOnly", "true")]
    public async Task RejectLayerOnlyShapes_rejects_flag_shapes(string key, string value)
    {
        var query = await ParseAsync((key, value));
        var exception = Assert.Throws<EsriInteropException>(() => FeatureServiceQuery.RejectLayerOnlyShapes(query));
        Assert.Contains(key, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectLayerOnlyShapes_rejects_statistics_shapes()
    {
        var query = await ParseAsync(
            ("outStatistics", "[{\"statisticType\":\"sum\",\"onStatisticField\":\"pop\",\"outStatisticFieldName\":\"s\"}]"));
        var exception = Assert.Throws<EsriInteropException>(() => FeatureServiceQuery.RejectLayerOnlyShapes(query));
        Assert.Contains("outStatistics", exception.Message, StringComparison.Ordinal);
    }
}
