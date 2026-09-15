using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices.Tests;

/// <summary>
/// T-040: the export <c>dynamicLayers</c> parameter (S1 dynamic-layer-table/,
/// S2 export-map/, research §2). Entries rebind a published layer by
/// <c>mapLayerId</c> and may override its renderer; anything the engine
/// cannot honour (new data sources, picture/text symbols, label overrides,
/// non-zero transparency) is a typed <c>invalid.arguments</c>, never a
/// silent fallback.
/// </summary>
public sealed class MapDynamicLayersTests
{
    private static readonly IReadOnlyList<PublishedLayer> Published =
    [
        new(0, "demo.cities", "Cities", """[{"type":"circle","paint":{"circle-color":"#0000ff"}}]"""),
        new(1, "demo.rivers", "Rivers"),
    ];

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_returns_null_without_input(string? value)
    {
        Assert.Null(MapDynamicLayers.Parse(value));
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("""{"id":0}""")]
    [InlineData("[1,2]")]
    [InlineData("""[{"source":{"type":"mapLayer","mapLayerId":0}}]""")]
    [InlineData("""[{"id":0}]""")]
    [InlineData("""[{"id":0,"source":{"type":"dataLayer"}}]""")]
    [InlineData("""[{"id":0,"source":{"type":"mapLayer"}}]""")]
    [InlineData("""[{"id":0,"source":{"type":"mapLayer","mapLayerId":0}},{"id":0,"source":{"type":"mapLayer","mapLayerId":1}}]""")]
    public void Parse_rejects_malformed_entries(string value)
    {
        var failure = Assert.Throws<EsriInteropException>(() => MapDynamicLayers.Parse(value));
        Assert.Equal(EsriErrorCodes.InvalidParameters, failure.Code);
    }

    [Fact]
    public void Parse_rejects_join_and_query_data_sources()
    {
        var failure = Assert.Throws<EsriInteropException>(() => MapDynamicLayers.Parse(
            """[{"id":5,"source":{"type":"dataLayer","dataSource":{"type":"joinTableDataSource"}}}]"""));
        Assert.Equal(EsriErrorCodes.InvalidParameters, failure.Code);
    }

    [Fact]
    public void Parse_rebinds_a_layer_without_a_style_override()
    {
        var overrides = MapDynamicLayers.Parse("""[{"id":7,"source":{"type":"mapLayer","mapLayerId":1}}]""");

        Assert.NotNull(overrides);
        var only = Assert.Single(overrides);
        Assert.Equal(7, only.Id);
        Assert.Equal(1, only.MapLayerId);
        Assert.Null(only.StyleOverride);
    }

    [Fact]
    public void Parse_converts_a_simple_marker_override_to_circles()
    {
        var overrides = MapDynamicLayers.Parse(
            """[{"id":0,"source":{"type":"mapLayer","mapLayerId":0},"drawingInfo":{"renderer":{"type":"simple","symbol":{"type":"esriSMS","style":"esriSMSCircle","color":[255,0,0,128],"size":12}}}}]""");

        Assert.NotNull(overrides);
        var style = Assert.Single(overrides).StyleOverride;
        Assert.NotNull(style);
        Assert.Contains("\"circle-color\":\"#ff0000\"", style);
        Assert.Contains("\"circle-radius\":6", style);
        Assert.Contains("\"circle-opacity\":0.502", style);
    }

    [Fact]
    public void Parse_converts_a_unique_value_override_to_filtered_circles()
    {
        var overrides = MapDynamicLayers.Parse(
            """[{"id":0,"source":{"type":"mapLayer","mapLayerId":0},"drawingInfo":{"renderer":{"type":"uniqueValue","field1":"country","uniqueValueInfos":[{"value":"Germany","symbol":{"type":"esriSMS","style":"esriSMSCircle","color":[255,0,0,255],"size":10}}]}}}]""");

        Assert.NotNull(overrides);
        var style = Assert.Single(overrides).StyleOverride;
        Assert.NotNull(style);
        Assert.Contains("country", style);
        Assert.Contains("Germany", style);
    }

    [Fact]
    public void Parse_converts_a_class_breaks_override_to_interval_circles()
    {
        var overrides = MapDynamicLayers.Parse(
            """[{"id":0,"source":{"type":"mapLayer","mapLayerId":0},"drawingInfo":{"renderer":{"type":"classBreaks","field":"population","minValue":0,"classBreakInfos":[{"classMaxValue":100,"symbol":{"type":"esriSMS","style":"esriSMSCircle","color":[255,255,0,255],"size":8}},{"classMaxValue":200,"symbol":{"type":"esriSMS","style":"esriSMSCircle","color":[255,0,0,255],"size":12}}]}}}]""");

        Assert.NotNull(overrides);
        var style = Assert.Single(overrides).StyleOverride;
        Assert.NotNull(style);
        Assert.Contains("population", style);
        Assert.Contains("100", style);
        Assert.Contains("200", style);
    }

    [Theory]
    [InlineData("bogus")]
    [InlineData("picture")]
    public void Parse_rejects_unknown_renderer_types(string type)
    {
        var failure = Assert.Throws<EsriInteropException>(() => MapDynamicLayers.Parse(
            "[{\"id\":0,\"source\":{\"type\":\"mapLayer\",\"mapLayerId\":0},\"drawingInfo\":{\"renderer\":{\"type\":\"" + type + "\",\"symbol\":{\"type\":\"esriSMS\",\"style\":\"esriSMSCircle\",\"color\":[0,0,0,255]}}}}]"));
        Assert.Equal(EsriErrorCodes.InvalidParameters, failure.Code);
    }

    [Fact]
    public void Parse_rejects_picture_symbols()
    {
        var failure = Assert.Throws<EsriInteropException>(() => MapDynamicLayers.Parse(
            """[{"id":0,"source":{"type":"mapLayer","mapLayerId":0},"drawingInfo":{"renderer":{"type":"simple","symbol":{"type":"esriPMS","url":"1","color":[0,0,0,255]}}}}]"""));
        Assert.Equal(EsriErrorCodes.InvalidParameters, failure.Code);
    }

    [Fact]
    public void Parse_rejects_label_overrides_and_fades()
    {
        var labels = Assert.Throws<EsriInteropException>(() => MapDynamicLayers.Parse(
            """[{"id":0,"source":{"type":"mapLayer","mapLayerId":0},"drawingInfo":{"renderer":{"type":"simple","symbol":{"type":"esriSMS","style":"esriSMSCircle","color":[0,0,0,255]}},"labelingInfo":[{"labelPlacement":"esriServerPointLabelPlacementCenterCenter"}]}}]"""));
        Assert.Equal(EsriErrorCodes.InvalidParameters, labels.Code);

        var faded = Assert.Throws<EsriInteropException>(() => MapDynamicLayers.Parse(
            """[{"id":0,"source":{"type":"mapLayer","mapLayerId":0},"drawingInfo":{"renderer":{"type":"simple","symbol":{"type":"esriSMS","style":"esriSMSCircle","color":[0,0,0,255]}},"transparency":50}}]"""));
        Assert.Equal(EsriErrorCodes.InvalidParameters, faded.Code);
    }

    [Fact]
    public void Apply_overrides_the_rebound_layer_style()
    {
        var overrides = MapDynamicLayers.Parse(
            """[{"id":0,"source":{"type":"mapLayer","mapLayerId":0},"drawingInfo":{"renderer":{"type":"simple","symbol":{"type":"esriSMS","style":"esriSMSCircle","color":[255,0,0,255],"size":10}}}}]""");

        var effective = MapDynamicLayers.Apply(Published, overrides, "world");

        Assert.Equal(2, effective.Count);
        Assert.NotEqual(Published[0].Style, effective[0].Style);
        Assert.Contains("#ff0000", effective[0].Style);
        Assert.Equal("demo.cities", effective[0].Dataset);
        Assert.Equal(Published[1], effective[1]);
    }

    [Fact]
    public void Apply_appends_a_new_dynamic_id()
    {
        var overrides = MapDynamicLayers.Parse("""[{"id":7,"source":{"type":"mapLayer","mapLayerId":1}}]""");

        var effective = MapDynamicLayers.Apply(Published, overrides, "world");

        Assert.Equal(3, effective.Count);
        Assert.Equal(7, effective[2].Id);
        Assert.Equal("demo.rivers", effective[2].Dataset);
    }

    [Fact]
    public void Apply_without_overrides_returns_the_published_layers()
    {
        Assert.Equal(Published, MapDynamicLayers.Apply(Published, null, "world"));
    }

    [Fact]
    public void Parse_converts_a_simple_line_override()
    {
        var overrides = MapDynamicLayers.Parse(
            """[{"id":1,"source":{"type":"mapLayer","mapLayerId":1},"drawingInfo":{"renderer":{"type":"simple","symbol":{"type":"esriSLS","style":"esriSLSSolid","color":[0,0,255,255],"width":3}}}}]""");

        Assert.NotNull(overrides);
        var style = Assert.Single(overrides).StyleOverride;
        Assert.NotNull(style);
        Assert.Contains("\"line-color\":\"#0000ff\"", style);
        Assert.Contains("\"line-width\":3", style);
        Assert.Contains("\"line-opacity\":1", style);
    }

    [Fact]
    public void Parse_rejects_non_solid_line_styles()
    {
        var failure = Assert.Throws<EsriInteropException>(() => MapDynamicLayers.Parse(
            """[{"id":1,"source":{"type":"mapLayer","mapLayerId":1},"drawingInfo":{"renderer":{"type":"simple","symbol":{"type":"esriSLS","style":"esriSLSDash","color":[0,0,0,255]}}}}]"""));
        Assert.Equal(EsriErrorCodes.InvalidParameters, failure.Code);
    }

    [Fact]
    public void Parse_converts_a_simple_fill_override()
    {
        var overrides = MapDynamicLayers.Parse(
            """[{"id":0,"source":{"type":"mapLayer","mapLayerId":0},"drawingInfo":{"renderer":{"type":"simple","symbol":{"type":"esriSFS","style":"esriSFSSolid","color":[0,128,0,128]}}}}]""");

        Assert.NotNull(overrides);
        var style = Assert.Single(overrides).StyleOverride;
        Assert.NotNull(style);
        Assert.Contains("\"fill-color\":\"#008000\"", style);
        Assert.Contains("\"fill-opacity\":0.502", style);
    }

    [Fact]
    public void Parse_converts_a_fill_outline_override()
    {
        var overrides = MapDynamicLayers.Parse(
            """[{"id":0,"source":{"type":"mapLayer","mapLayerId":0},"drawingInfo":{"renderer":{"type":"simple","symbol":{"type":"esriSFS","style":"esriSFSSolid","color":[0,0,255,255],"outline":{"color":[255,0,0,255],"width":2}}}}}]""");

        Assert.NotNull(overrides);
        var style = Assert.Single(overrides).StyleOverride;
        Assert.NotNull(style);
        Assert.Contains("\"fill-outline-color\":\"#ff0000\"", style);
        Assert.Contains("\"fill-outline-width\":2", style);
    }

    [Fact]
    public void Parse_rejects_non_solid_fill_styles()
    {
        var failure = Assert.Throws<EsriInteropException>(() => MapDynamicLayers.Parse(
            """[{"id":0,"source":{"type":"mapLayer","mapLayerId":0},"drawingInfo":{"renderer":{"type":"simple","symbol":{"type":"esriSFS","style":"esriSFSNull","color":[0,0,0,255]}}}}]"""));
        Assert.Equal(EsriErrorCodes.InvalidParameters, failure.Code);
    }

    [Fact]
    public void Apply_naming_an_unknown_layer_is_not_found()
    {
        var overrides = MapDynamicLayers.Parse("""[{"id":7,"source":{"type":"mapLayer","mapLayerId":42}}]""");

        var failure = Assert.Throws<EsriInteropException>(() => MapDynamicLayers.Apply(Published, overrides, "world"));
        Assert.Equal(EsriErrorCodes.NotFound, failure.Code);
    }
}
