using System.Text.Json;
using Spatial.PluginSdk;
using Spatial.Rendering.Skia.Styling;

namespace Spatial.Rendering.Skia.Tests;

public sealed class SymbolReaderTests
{
    [Fact]
    public void Read_UsesDocumentedDefaults()
    {
        var paint = PaintReader.Read(DrawKind.Symbol, Paint(), Layout("""{ "text-field": "{name}" }"""));

        var options = Assert.IsType<SymbolPaint>(paint).Options;
        Assert.Equal("{name}", options.TextField);
        Assert.Empty(options.Fonts);
        Assert.Equal(16, options.Size);
        Assert.Equal(new StyleColor(0, 0, 0), options.Color);
        Assert.Equal(StyleColor.Transparent, options.HaloColor);
        Assert.Equal(0, options.HaloWidth);
        Assert.Equal(SymbolAnchor.Center, options.Anchor);
        Assert.Equal((0.0, 0.0), (options.OffsetX, options.OffsetY));
        Assert.Equal(2, options.Padding);
        Assert.False(options.AllowTextOverlap);
        Assert.Null(options.IconImage);
        Assert.Equal(1, options.IconSize);
        Assert.False(options.AllowIconOverlap);
    }

    [Fact]
    public void Read_ReadsEverySupportedSymbolProperty()
    {
        var layout = Layout(
            """
            {
              "visibility": "visible",
              "text-field": "{name}",
              "text-font": ["Noto Sans Regular"],
              "text-size": 18,
              "text-anchor": "bottom-right",
              "text-offset": [1, -0.5],
              "text-padding": 4,
              "text-allow-overlap": true,
              "icon-image": "default-marker",
              "icon-size": 1.5,
              "icon-allow-overlap": true
            }
            """);
        var paint = Paint(
            """{ "text-color": "#ff0000", "text-opacity": 0.5, "text-halo-color": "#ffffff", "text-halo-width": 2 }""");

        var options = Assert.IsType<SymbolPaint>(PaintReader.Read(DrawKind.Symbol, paint, layout)).Options;

        Assert.Equal("{name}", options.TextField);
        Assert.Equal(["Noto Sans Regular"], options.Fonts);
        Assert.Equal(18, options.Size);
        Assert.Equal(new StyleColor(255, 0, 0, 128), options.Color);
        Assert.Equal(new StyleColor(255, 255, 255, 128), options.HaloColor);
        Assert.Equal(2, options.HaloWidth);
        Assert.Equal(SymbolAnchor.BottomRight, options.Anchor);
        Assert.Equal((1.0, -0.5), (options.OffsetX, options.OffsetY));
        Assert.Equal(4, options.Padding);
        Assert.True(options.AllowTextOverlap);
        Assert.Equal("default-marker", options.IconImage);
        Assert.Equal(1.5, options.IconSize);
        Assert.True(options.AllowIconOverlap);
    }

    [Theory]
    [InlineData("center", 0)]
    [InlineData("left", 1)]
    [InlineData("right", 2)]
    [InlineData("top", 3)]
    [InlineData("bottom", 4)]
    [InlineData("top-left", 5)]
    [InlineData("top-right", 6)]
    [InlineData("bottom-left", 7)]
    [InlineData("bottom-right", 8)]
    public void Read_AcceptsEveryAnchor(string anchor, int expected)
    {
        var layout = Layout($$"""{ "text-field": "{name}", "text-anchor": "{{anchor}}" }""");
        var options = Assert.IsType<SymbolPaint>(PaintReader.Read(DrawKind.Symbol, Paint(), layout)).Options;
        Assert.Equal((SymbolAnchor)expected, options.Anchor);
    }

    [Fact]
    public void Read_RequiresTextFieldOrIconImage()
    {
        Assert.Throws<SpatialException>(() => PaintReader.Read(DrawKind.Symbol, Paint(), Layout("{}")));
    }

    [Theory]
    [InlineData("""{ "text-field": 12 }""")]
    [InlineData("""{ "text-field": ["get", "name"] }""")]
    [InlineData("""{ "text-size": "big" }""")]
    [InlineData("""{ "text-size": -1 }""")]
    [InlineData("""{ "text-padding": -1 }""")]
    [InlineData("""{ "text-anchor": "middle" }""")]
    [InlineData("""{ "text-offset": [1] }""")]
    [InlineData("""{ "text-offset": [1, "x"] }""")]
    [InlineData("""{ "text-allow-overlap": "yes" }""")]
    [InlineData("""{ "icon-size": -1 }""")]
    [InlineData("""{ "symbol-placement": "line" }""")]
    [InlineData("""{ "text-font": "Noto Sans" }""")]
    [InlineData("""{ "text-font": ["Comic Sans MS"] }""")]
    [InlineData("""{ "icon-padding": 3 }""")]
    public void Read_RejectsUnsupportedLayout(string layout)
    {
        Assert.Throws<SpatialException>(() => PaintReader.Read(DrawKind.Symbol, Paint(), Layout(layout)));
    }

    [Theory]
    [InlineData("""{ "text-color": "not-a-colour" }""")]
    [InlineData("""{ "text-halo-width": -1 }""")]
    [InlineData("""{ "text-opacity": "half" }""")]
    [InlineData("""{ "text-transform": "uppercase" }""")]
    [InlineData("""{ "icon-color": "#fff" }""")]
    public void Read_RejectsUnsupportedPaint(string paint)
    {
        Assert.Throws<SpatialException>(() => PaintReader.Read(DrawKind.Symbol, Layout("""{ "text-field": "{name}" }"""), Layout(paint)));
    }

    [Fact]
    public void Read_RejectsNonObjectLayout()
    {
        Assert.Throws<SpatialException>(() => PaintReader.Read(DrawKind.Symbol, Paint(), Layout("42")));
    }

    private static JsonElement Paint(string? json = null) => Json(json ?? "{}");

    private static JsonElement Layout(string json) => Json(json);

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
