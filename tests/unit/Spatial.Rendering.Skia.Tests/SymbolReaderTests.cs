using System.Text.Json;
using Spatial.Contracts;
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
        Assert.Equal(SymbolPlacement.Point, options.Placement);
        Assert.Equal(250, options.Spacing);
        Assert.Equal(0, options.SortKey);
        Assert.Null(options.AllowOverlap);
        Assert.False(options.IgnorePlacement);
        Assert.Equal(SymbolTextTransform.None, options.TextTransform);
        Assert.Equal(0, options.LetterSpacing);
        Assert.Equal(1.2, options.LineHeight);
        Assert.Equal(0, options.Rotate);
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

    [Fact]
    public void Read_ReadsEveryPlacementAndTextTransformProperty()
    {
        var layout = Layout(
            """
            {
              "text-field": "{name}",
              "symbol-placement": "line",
              "symbol-spacing": 64,
              "symbol-sort-key": 3,
              "symbol-allow-overlap": true,
              "symbol-ignore-placement": true,
              "text-transform": "uppercase",
              "text-letter-spacing": 0.1,
              "text-line-height": 1.5,
              "text-rotate": 30
            }
            """);

        var options = Assert.IsType<SymbolPaint>(PaintReader.Read(DrawKind.Symbol, Paint(), layout)).Options;

        Assert.Equal(SymbolPlacement.Line, options.Placement);
        Assert.Equal(64, options.Spacing);
        Assert.Equal(3, options.SortKey);
        Assert.True(options.AllowOverlap);
        Assert.True(options.IgnorePlacement);
        Assert.Equal(SymbolTextTransform.Uppercase, options.TextTransform);
        Assert.Equal(0.1, options.LetterSpacing);
        Assert.Equal(1.5, options.LineHeight);
        Assert.Equal(30, options.Rotate);
    }

    [Fact]
    public void Read_SymbolAllowOverlapDefaultsToThePerComponentFlags()
    {
        var layout = Layout("""{ "text-field": "{name}", "text-allow-overlap": true }""");

        var options = Assert.IsType<SymbolPaint>(PaintReader.Read(DrawKind.Symbol, Paint(), layout)).Options;

        Assert.Null(options.AllowOverlap);
    }

    [Theory]
    [InlineData("none", 0)]
    [InlineData("uppercase", 1)]
    [InlineData("lowercase", 2)]
    public void Read_AcceptsEveryTextTransform(string transform, int expected)
    {
        var layout = Layout($$"""{ "text-field": "{name}", "text-transform": "{{transform}}" }""");

        var options = Assert.IsType<SymbolPaint>(PaintReader.Read(DrawKind.Symbol, Paint(), layout)).Options;

        Assert.Equal((SymbolTextTransform)expected, options.TextTransform);
    }

    /// <summary>
    /// The fallback chain never rejects a family: a name the bundle does not
    /// carry resolves to the first available face at draw time, so a style
    /// asking for a font the engine has never heard of still renders.
    /// </summary>
    [Theory]
    [InlineData("""["Comic Sans MS"]""")]
    [InlineData("""["Helvetica Neue", "Comic Sans MS"]""")]
    [InlineData("""[]""")]
    public void Read_AcceptsAnyFontFamilyAndResolvesItAtDrawTime(string fonts)
    {
        var layout = Layout($$"""{ "text-field": "{name}", "text-font": {{fonts}} }""");

        var options = Assert.IsType<SymbolPaint>(PaintReader.Read(DrawKind.Symbol, Paint(), layout)).Options;

        Assert.NotNull(options);
    }

    [Theory]
    [InlineData("symbol-placement", "\"curved\"")]
    [InlineData("symbol-spacing", "0")]
    [InlineData("symbol-spacing", "-1")]
    [InlineData("symbol-sort-key", "\"high\"")]
    [InlineData("symbol-allow-overlap", "\"yes\"")]
    [InlineData("symbol-ignore-placement", "1")]
    [InlineData("text-transform", "\"titlecase\"")]
    [InlineData("text-letter-spacing", "-0.2")]
    [InlineData("text-line-height", "0")]
    [InlineData("text-line-height", "-1")]
    [InlineData("text-rotate", "\"90\"")]
    public void Read_RejectsUnsupportedPlacementValues(string property, string value)
    {
        Assert.Throws<SpatialException>(() => PaintReader.Read(DrawKind.Symbol, Paint(), Layout(
            $$"""{ "text-field": "{name}", "{{property}}": {{value}} }""")));
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
    [InlineData("""{ "text-font": "Noto Sans" }""")]
    [InlineData("""{ "icon-padding": 3 }""")]
    [InlineData("""{ "symbol-avoid-edges": true }""")]
    [InlineData("""{ "symbol-z-order": "source" }""")]
    [InlineData("""{ "text-rotation-alignment": "map" }""")]
    public void Read_RejectsUnsupportedLayout(string layout)
    {
        Assert.Throws<SpatialException>(() => PaintReader.Read(DrawKind.Symbol, Paint(), Layout(layout)));
    }

    [Theory]
    [InlineData("""{ "text-color": "not-a-colour" }""")]
    [InlineData("""{ "text-halo-width": -1 }""")]
    [InlineData("""{ "text-opacity": "half" }""")]
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
