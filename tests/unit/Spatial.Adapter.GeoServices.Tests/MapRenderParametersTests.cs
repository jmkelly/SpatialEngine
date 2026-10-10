using Spatial.Contracts;
using Spatial.Core.Geometry;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices.Tests;

/// <summary>
/// The MapServer image-render request grammar (spec §4.0.4/§4.1,
/// ADR-0048): the <c>layerDefs</c> safe-filter grammar plus the format, bbox,
/// size and dpi parameters. Client text is re-rendered, never passed through.
/// </summary>
public sealed class MapRenderParametersTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseLayerDefs_returns_null_without_input(string? value)
    {
        Assert.Null(MapRenderParameters.ParseLayerDefs(value));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[1,2,3]")]
    [InlineData("\"text\"")]
    public void ParseLayerDefs_rejects_non_object_json(string value)
    {
        var failure = Assert.Throws<EsriInteropException>(() => MapRenderParameters.ParseLayerDefs(value));
        Assert.Equal(EsriErrorCodes.InvalidParameters, failure.Code);
    }

    [Fact]
    public void ParseLayerDefs_rejects_non_integer_layer_names()
    {
        var failure = Assert.Throws<EsriInteropException>(() => MapRenderParameters.ParseLayerDefs("""{"roads":"name = 'x'"}"""));
        Assert.Equal(EsriErrorCodes.InvalidParameters, failure.Code);
    }

    [Fact]
    public void ParseLayerDefs_rejects_unsupported_clauses()
    {
        var failure = Assert.Throws<EsriInteropException>(() => MapRenderParameters.ParseLayerDefs("""{"0":"name = "}"""));
        Assert.Equal(EsriErrorCodes.InvalidParameters, failure.Code);
    }

    [Theory]
    [InlineData("""{"0":""}""")]
    [InlineData("""{"0":"   "}""")]
    [InlineData("""{"0":123}""")]
    [InlineData("""{"0":null}""")]
    public void ParseLayerDefs_skips_blank_or_non_string_clauses(string value)
    {
        Assert.Null(MapRenderParameters.ParseLayerDefs(value));
    }

    [Fact]
    public void ParseLayerDefs_compiles_each_clause_to_the_shared_predicate()
    {
        var defs = MapRenderParameters.ParseLayerDefs("""{"0":"name = 'x'","2":"population >= 5","7":""}""");

        Assert.NotNull(defs);
        Assert.Equal(2, defs.Count);
        Assert.Equal("name = 'x'", defs[0].ToWhere());
        Assert.Equal("population >= 5", defs[2].ToWhere());
        Assert.Equal(["name"], defs[0].ReferencedFields);
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
        Assert.Equal(expected, MapRenderParameters.ParseFormat(value));
    }

    [Fact]
    public void ParseFormat_rejects_unknown_containers()
    {
        Assert.Throws<EsriInteropException>(() => MapRenderParameters.ParseFormat("bmp"));
    }

    [Fact]
    public void ParseBbox_reads_four_numbers_and_requires_them()
    {
        Assert.Equal(new Envelope(1, 2, 3, 4), MapRenderParameters.ParseBbox("1,2,3,4"));
        Assert.Throws<EsriInteropException>(() => MapRenderParameters.ParseBbox(null));
        Assert.Throws<EsriInteropException>(() => MapRenderParameters.ParseBbox("1,2,3"));
    }

    [Fact]
    public void FitExtent_expands_the_shorter_side_to_the_image_aspect()
    {
        // The parity frame (spec §export: "the extent should be resized to
        // prevent map images from appearing stretched"): a 59x25 degree
        // bbox at 800x600 grows vertically, centered, instead of stretching.
        var fitted = MapRenderParameters.FitExtent(new Envelope(-125, 25, -66, 50), 800, 600);

        Assert.Equal(-125, fitted.MinX);
        Assert.Equal(-66, fitted.MaxX);
        var height = 59.0 * 600 / 800;
        Assert.Equal(37.5 - height / 2, fitted.MinY, 9);
        Assert.Equal(37.5 + height / 2, fitted.MaxY, 9);
    }

    [Fact]
    public void FitExtent_expands_horizontally_for_tall_frames()
    {
        var fitted = MapRenderParameters.FitExtent(new Envelope(0, 0, 10, 40), 200, 400);

        Assert.Equal(0, fitted.MinY);
        Assert.Equal(40, fitted.MaxY);
        Assert.Equal(5 - 10, fitted.MinX, 9);
        Assert.Equal(5 + 10, fitted.MaxX, 9);
    }

    [Fact]
    public void FitExtent_keeps_a_matching_frame_unchanged()
    {
        var bounds = new Envelope(-20, 20, 40, 70);
        var fitted = MapRenderParameters.FitExtent(bounds, 600, 500);

        Assert.Equal(bounds.MinX, fitted.MinX);
        Assert.Equal(bounds.MinY, fitted.MinY);
        Assert.Equal(bounds.MaxX, fitted.MaxX);
        Assert.Equal(bounds.MaxY, fitted.MaxY);
    }

    [Fact]
    public void ParseSize_reads_positive_dimensions()
    {
        Assert.Equal((10, 20), MapRenderParameters.ParseSize("10,20"));
        Assert.Throws<EsriInteropException>(() => MapRenderParameters.ParseSize(null));
        Assert.Throws<EsriInteropException>(() => MapRenderParameters.ParseSize("10"));
        Assert.Throws<EsriInteropException>(() => MapRenderParameters.ParseSize("0,20"));
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
        Assert.Equal(expected, MapRenderParameters.Dpi(value));
    }
}
