using Spatial.Core.Geometry;
using Spatial.Esri.Codec;
using Spatial.PluginSdk;

namespace Spatial.Adapter.GeoServices.Tests;

/// <summary>
/// The MapServer render bridge's parameter parsing (spec §4.0.4/§4.1,
/// ADR-0048): the <c>layerDefs</c> safe-filter grammar plus the format, bbox,
/// size and dpi parameters. Client text is re-rendered, never passed through.
/// </summary>
public sealed class MapRenderEngineTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseLayerDefs_returns_null_without_input(string? value)
    {
        Assert.Null(MapRenderEngine.ParseLayerDefs(value));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[1,2,3]")]
    [InlineData("\"text\"")]
    public void ParseLayerDefs_rejects_non_object_json(string value)
    {
        var failure = Assert.Throws<EsriInteropException>(() => MapRenderEngine.ParseLayerDefs(value));
        Assert.Equal(EsriErrorCodes.InvalidParameters, failure.Code);
    }

    [Fact]
    public void ParseLayerDefs_rejects_non_integer_layer_names()
    {
        var failure = Assert.Throws<EsriInteropException>(() => MapRenderEngine.ParseLayerDefs("""{"roads":"name = 'x'"}"""));
        Assert.Equal(EsriErrorCodes.InvalidParameters, failure.Code);
    }

    [Fact]
    public void ParseLayerDefs_rejects_unsupported_clauses()
    {
        var failure = Assert.Throws<EsriInteropException>(() => MapRenderEngine.ParseLayerDefs("""{"0":"name = "}"""));
        Assert.Equal(EsriErrorCodes.InvalidParameters, failure.Code);
    }

    [Theory]
    [InlineData("""{"0":""}""")]
    [InlineData("""{"0":"   "}""")]
    [InlineData("""{"0":123}""")]
    [InlineData("""{"0":null}""")]
    public void ParseLayerDefs_skips_blank_or_non_string_clauses(string value)
    {
        Assert.Null(MapRenderEngine.ParseLayerDefs(value));
    }

    [Fact]
    public void ParseLayerDefs_parses_and_rerenders_each_clause()
    {
        var defs = MapRenderEngine.ParseLayerDefs("""{"0":"name = 'x'","2":"population >= 5","7":""}""");

        Assert.NotNull(defs);
        Assert.Equal(2, defs.Count);
        Assert.Equal("name = 'x'", defs[0]);
        Assert.Equal("population >= 5", defs[2]);
    }

    [Theory]
    [InlineData(null, RasterFormat.Png)]
    [InlineData("", RasterFormat.Png)]
    [InlineData("png", RasterFormat.Png)]
    [InlineData("PNG32", RasterFormat.Png)]
    [InlineData("jpg", RasterFormat.Jpeg)]
    [InlineData("jpeg", RasterFormat.Jpeg)]
    [InlineData("webp", RasterFormat.Webp)]
    [InlineData("tif", RasterFormat.Tiff)]
    [InlineData("TIFF", RasterFormat.Tiff)]
    public void ParseFormat_accepts_the_supported_containers(string? value, RasterFormat expected)
    {
        Assert.Equal(expected, MapRenderEngine.ParseFormat(value));
    }

    [Fact]
    public void ParseFormat_rejects_unknown_containers()
    {
        Assert.Throws<EsriInteropException>(() => MapRenderEngine.ParseFormat("bmp"));
    }

    [Fact]
    public void ParseBbox_reads_four_numbers_and_requires_them()
    {
        Assert.Equal(new Envelope(1, 2, 3, 4), MapRenderEngine.ParseBbox("1,2,3,4"));
        Assert.Throws<EsriInteropException>(() => MapRenderEngine.ParseBbox(null));
        Assert.Throws<EsriInteropException>(() => MapRenderEngine.ParseBbox("1,2,3"));
    }

    [Fact]
    public void ParseSize_reads_positive_dimensions()
    {
        Assert.Equal((10, 20), MapRenderEngine.ParseSize("10,20"));
        Assert.Throws<EsriInteropException>(() => MapRenderEngine.ParseSize(null));
        Assert.Throws<EsriInteropException>(() => MapRenderEngine.ParseSize("10"));
        Assert.Throws<EsriInteropException>(() => MapRenderEngine.ParseSize("0,20"));
    }

    [Theory]
    [InlineData(null, 96)]
    [InlineData("", 96)]
    [InlineData("0", 96)]
    [InlineData("-5", 96)]
    [InlineData("not-a-number", 96)]
    [InlineData("150", 150)]
    public void Dpi_defaults_to_96(string? value, double expected)
    {
        Assert.Equal(expected, MapRenderEngine.Dpi(value));
    }
}
