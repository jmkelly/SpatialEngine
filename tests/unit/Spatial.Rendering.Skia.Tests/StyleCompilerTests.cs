using Spatial.PluginSdk;
using Spatial.Rendering.Skia.Styling;

namespace Spatial.Rendering.Skia.Tests;

public sealed class StyleCompilerTests
{
    private const string ValidStyle = """
        {
          "version": 8,
          "layers": [
            { "id": "bg", "type": "background", "paint": { "background-color": "#101820" } },
            { "id": "zones", "type": "fill", "source-layer": "demo.zones", "minzoom": 5, "maxzoom": 10,
              "filter": ["has", "name"], "paint": { "fill-color": "#2e7d32" } },
            { "id": "roads", "type": "line", "source-layer": "demo.roads",
              "layout": { "visibility": "none" }, "paint": { "line-width": 2 } },
            { "id": "cities", "type": "circle", "source-layer": "demo.cities", "paint": { "circle-radius": 4 } }
          ]
        }
        """;

    [Fact]
    public void Compile_ReadsTheDocumentedSubset()
    {
        var style = new StyleCompiler().Compile(ValidStyle);

        Assert.Equal(4, style.Layers.Count);
        Assert.Equal(DrawKind.Background, style.Layers[0].Kind);
        Assert.Null(style.Layers[0].Dataset);
        Assert.Equal("demo.zones", style.Layers[1].Dataset);
        Assert.Equal(5, style.Layers[1].MinZoom);
        Assert.Equal(10, style.Layers[1].MaxZoom);
        Assert.True(style.Layers[1].IsVisibleAt(5));
        Assert.False(style.Layers[1].IsVisibleAt(10));
        Assert.IsType<HasFilter>(style.Layers[1].Filter);
        Assert.False(style.Layers[2].Visible);
        Assert.IsType<FillPaint>(style.Layers[1].Paint);
        Assert.IsType<LinePaint>(style.Layers[2].Paint);
        Assert.IsType<CirclePaint>(style.Layers[3].Paint);
    }

    [Fact]
    public void Compile_CachesPlansByStyleContent()
    {
        var compiler = new StyleCompiler();
        var first = compiler.Compile(ValidStyle);
        var second = compiler.Compile(ValidStyle);
        Assert.Same(first, second);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{ \"version\": 8 }")]
    [InlineData("{ \"version\": 8, \"layers\": {} }")]
    [InlineData("{ \"version\": 8, \"layers\": [] }")]
    [InlineData("{ \"version\": 8, \"layers\": [ { \"id\": \"x\", \"type\": \"symbol\", \"source-layer\": \"a\" } ] }")]
    [InlineData("{ \"version\": 8, \"layers\": [ { \"id\": \"x\", \"type\": \"circle\" } ] }")]
    [InlineData("{ \"version\": 8, \"layers\": [ { \"type\": \"circle\", \"source-layer\": \"a\" } ] }")]
    [InlineData("{ \"version\": 8, \"layers\": [ { \"id\": \"x\", \"source-layer\": \"a\" } ] }")]
    [InlineData("{ \"version\": 8, \"layers\": [ { \"id\": \"x\", \"type\": \"circle\", \"source-layer\": \"a\", \"minzoom\": 10, \"maxzoom\": 5 } ] }")]
    [InlineData("{ \"version\": 8, \"layers\": [ { \"id\": \"x\", \"type\": \"circle\", \"source-layer\": \"a\", \"minzoom\": \"low\" } ] }")]
    [InlineData("{ \"version\": 8, \"layers\": [ { \"id\": \"x\", \"type\": \"circle\", \"source-layer\": \"a\", \"layout\": { \"visibility\": \"maybe\" } } ] }")]
    [InlineData("{ \"version\": 8, \"layers\": [ { \"id\": \"x\", \"type\": \"circle\", \"source-layer\": \"a\", \"paint\": { \"circle-colour\": \"#fff\" } } ] }")]
    [InlineData("{ \"version\": 8, \"layers\": [ 42 ] }")]
    public void Compile_RejectsUnsupportedDocuments(string style)
    {
        Assert.Throws<SpatialException>(() => new StyleCompiler().Compile(style));
    }

    [Fact]
    public void Compile_AcceptsAnExplicitVisibleLayout()
    {
        var style = new StyleCompiler().Compile(
            """{ "version": 8, "layers": [ { "id": "x", "type": "circle", "source-layer": "a", "layout": { "visibility": "visible" } } ] }""");
        Assert.True(style.Layers[0].Visible);
    }
}
