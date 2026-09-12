using System.Text.Json;
using Spatial.PluginSdk;
using Spatial.Rendering.Skia.Styling;

namespace Spatial.Rendering.Skia.Tests;

public sealed class PaintReaderTests
{
    [Fact]
    public void Read_UsesDocumentedDefaults()
    {
        Assert.Equal(
            new BackgroundPaint(StyleColor.Transparent),
            PaintReader.Read(DrawKind.Background, default));
        Assert.Equal(
            new CirclePaint(new StyleColor(0, 0, 0), 5, StyleColor.Transparent, 0),
            PaintReader.Read(DrawKind.Circle, default));
        Assert.Equal(
            new FillPaint(new StyleColor(0, 0, 0), StyleColor.Transparent, 0),
            PaintReader.Read(DrawKind.Fill, default));

        var line = Assert.IsType<LinePaint>(PaintReader.Read(DrawKind.Line, default));
        Assert.Equal(new StyleColor(0, 0, 0), line.Stroke);
        Assert.Equal(1, line.Width);
        Assert.Empty(line.Dash);
        Assert.Equal(LineCapStyle.Butt, line.Cap);
        Assert.Equal(LineJoinStyle.Miter, line.Join);
    }

    [Fact]
    public void Read_ReadsFillPaint()
    {
        var paint = PaintReader.Read(DrawKind.Fill, Json(
            """{ "fill-color": "#2e7d32", "fill-opacity": 0.5, "fill-outline-color": "#ffffff", "fill-outline-width": 2 }"""));

        var fill = Assert.IsType<FillPaint>(paint);
        Assert.Equal(new StyleColor(46, 125, 50, 128), fill.Fill);
        Assert.Equal(new StyleColor(255, 255, 255, 128), fill.Outline);
        Assert.Equal(2, fill.OutlineWidth);
    }

    [Fact]
    public void Read_ReadsLinePaint()
    {
        var paint = PaintReader.Read(DrawKind.Line, Json(
            """{ "line-color": "red", "line-width": 3, "line-opacity": 0.5, "line-dasharray": [4, 2], "line-cap": "round", "line-join": "bevel" }"""));

        var line = Assert.IsType<LinePaint>(paint);
        Assert.Equal(new StyleColor(255, 0, 0, 128), line.Stroke);
        Assert.Equal(3, line.Width);
        Assert.Equal([4, 2], line.Dash);
        Assert.Equal(LineCapStyle.Round, line.Cap);
        Assert.Equal(LineJoinStyle.Bevel, line.Join);
    }

    [Theory]
    [InlineData("""{ "line-cap": "flat" }""")]
    [InlineData("""{ "line-join": "pointy" }""")]
    [InlineData("""{ "circle-radius": -1 }""")]
    [InlineData("""{ "circle-stroke-width": -1 }""")]
    [InlineData("""{ "fill-outline-width": -1 }""")]
    [InlineData("""{ "circle-radius": "big" }""")]
    [InlineData("""{ "circle-radius": { "stops": [] } }""")]
    [InlineData("""{ "line-dasharray": 4 }""")]
    [InlineData("""{ "line-dasharray": [4, "x"] }""")]
    [InlineData("""{ "unsupported-property": 1 }""")]
    public void Read_RejectsUnsupportedPaint(string paint)
    {
        Assert.Throws<SpatialException>(() => PaintReader.Read(DrawKind.Circle, Json(paint)));
    }

    [Fact]
    public void Read_RejectsNonObjectPaint()
    {
        Assert.Throws<SpatialException>(() => PaintReader.Read(DrawKind.Fill, Json("42")));
    }

    [Fact]
    public void Read_RejectsInvalidColorValue()
    {
        Assert.Throws<SpatialException>(() => PaintReader.Read(DrawKind.Fill, Json("""{ "fill-color": "not-a-colour" }""")));
        Assert.Throws<SpatialException>(() => PaintReader.Read(DrawKind.Fill, Json("""{ "fill-color": 12 }""")));
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
