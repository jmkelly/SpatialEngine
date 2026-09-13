using Spatial.Adapter.GeoServices;

namespace Spatial.Adapter.GeoServices.Tests;

/// <summary>
/// The persisted MapLibre style to Esri <c>drawingInfo</c> projection
/// (spec §12, ADR-0048): a fill becomes an <c>esriSFS</c>, a line an
/// <c>esriSLS</c> and a circle an <c>esriSMS</c>, with opacity folded into
/// the RGBA quad. Richer renderers are deliberately absent.
/// </summary>
public sealed class MapStyleProjectionTests
{
    [Fact]
    public void A_fill_fragment_becomes_a_solid_fill_symbol()
    {
        var drawing = MapStyleProjection.Project(
            """[{"type":"fill","paint":{"fill-color":"#112233","fill-opacity":0.5,"fill-outline-color":"#ffffff","fill-outline-width":2}}]""");

        var symbol = drawing!.Renderer.Symbol;
        Assert.Equal("simple", drawing.Renderer.Type);
        Assert.Equal("esriSFS", symbol.Type);
        Assert.Equal("esriSFSSolid", symbol.Style);
        Assert.Equal([0x11, 0x22, 0x33, 128], symbol.Color);
        Assert.Equal("esriSLS", symbol.Outline!.Type);
        Assert.Equal(2, symbol.Outline.Width);
    }

    [Fact]
    public void A_line_fragment_becomes_a_solid_line_symbol()
    {
        var symbol = MapStyleProjection.Project("""[{"type":"line","paint":{"line-color":"#ff0000","line-width":3}}]""")!.Renderer.Symbol;

        Assert.Equal("esriSLS", symbol.Type);
        Assert.Equal(3, symbol.Width);
        Assert.Equal([255, 0, 0, 255], symbol.Color);
    }

    [Fact]
    public void A_circle_fragment_becomes_a_circle_marker_with_diameter_size()
    {
        var symbol = MapStyleProjection.Project(
            """[{"type":"circle","paint":{"circle-color":"#00ff00","circle-radius":7,"circle-stroke-color":"#000000","circle-stroke-width":1}}]""")!.Renderer.Symbol;

        Assert.Equal("esriSMS", symbol.Type);
        Assert.Equal("esriSMSCircle", symbol.Style);
        Assert.Equal(14, symbol.Size);
        Assert.Equal([0, 255, 0, 255], symbol.Color);
        Assert.Equal("esriSLS", symbol.Outline!.Type);
    }

    [Fact]
    public void Fill_precedes_line_and_circle_when_several_fragments_exist()
    {
        var symbol = MapStyleProjection.Project(
            """[{"type":"circle","paint":{"circle-color":"#000000"}},{"type":"fill","paint":{"fill-color":"#abcdef"}}]""")!.Renderer.Symbol;

        Assert.Equal("esriSFS", symbol.Type);
        Assert.Equal([0xab, 0xcd, 0xef, 255], symbol.Color);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void No_usable_style_yields_no_drawing_info(string? style) =>
        Assert.Null(MapStyleProjection.Project(style));

    [Fact]
    public void An_opacity_scales_the_symbol_alpha()
    {
        var symbol = MapStyleProjection.Project("""[{"type":"line","paint":{"line-color":"#123456","line-opacity":0.25}}]""")!.Renderer.Symbol;

        Assert.Equal(64, symbol.Color[3]);
    }

    [Fact]
    public void Named_and_functional_colors_are_understood()
    {
        Assert.Equal([255, 0, 0, 255], Color("red"));
        Assert.Equal([1, 2, 3, 128], Color("rgba(1,2,3,0.5)"));
        Assert.Equal([0, 0, 0, 0], Color("transparent"));
    }

    private static int[] Color(string css) =>
        MapStyleProjection.Project("[{\"type\":\"line\",\"paint\":{\"line-color\":\"" + css + "\"}}]")!.Renderer.Symbol.Color.ToArray();
}
