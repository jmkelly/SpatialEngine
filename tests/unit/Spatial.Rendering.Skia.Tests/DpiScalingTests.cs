using Spatial.Contracts;
using Spatial.Rendering.Skia.Pipeline;
using Spatial.Rendering.Skia.Styling;

namespace Spatial.Rendering.Skia.Tests;

/// <summary>
/// DPI scaling (T-045 item 3): style pixel sizes authored at
/// <see cref="MapRenderRequest.ReferenceDpi"/> grow linearly with the request
/// DPI, while colours, geometry placement and the output frame are untouched.
/// </summary>
public sealed class DpiScalingTests
{
    [Theory]
    [InlineData(96.0, false)]
    [InlineData(192.0, true)]
    [InlineData(48.0, true)]
    [InlineData(double.NaN, false)]
    [InlineData(double.PositiveInfinity, false)]
    public void NeedsScaling_FlagsNonReferenceDpi(double dpi, bool expected)
    {
        Assert.Equal(expected, DpiScaling.NeedsScaling(dpi));
    }

    [Fact]
    public void Apply_DoublesEveryPaintSize_AtTwiceTheReferenceDpi()
    {
        var style = new CompiledStyle(
        [
            Layer("background", new BackgroundPaint(new StyleColor(10, 20, 30))),
            Layer("fill", new FillPaint(new StyleColor(1, 2, 3), new StyleColor(4, 5, 6), 2)),
            Layer("line", new LinePaint(new StyleColor(7, 8, 9), 3, [4, 5], LineCapStyle.Round, LineJoinStyle.Bevel)),
            Layer("circle", new CirclePaint(new StyleColor(1, 1, 1), 5, new StyleColor(2, 2, 2), 1.5)),
            Layer("symbol", new SymbolPaint(new SymbolOptions(
                "name", ["Arial"], 12, new StyleColor(1, 2, 3), new StyleColor(4, 5, 6), 2,
                SymbolAnchor.Center, 3, 4, 5, false, "icon", 6, false))),
        ]);

        var scaled = DpiScaling.Apply(style, 192);

        Assert.Equal(5, scaled.Layers.Count);
        // Colours and layer identity survive; only sizes double.
        Assert.Equal(style.Layers[0].Paint, scaled.Layers[0].Paint);
        Assert.Equal(
            new FillPaint(new StyleColor(1, 2, 3), new StyleColor(4, 5, 6), 4),
            scaled.Layers[1].Paint);
        var line = Assert.IsType<LinePaint>(scaled.Layers[2].Paint);
        Assert.Equal(6, line.Width);
        Assert.Equal([8.0, 10.0], line.Dash);
        Assert.Equal(LineCapStyle.Round, line.Cap);
        Assert.Equal(
            new CirclePaint(new StyleColor(1, 1, 1), 10, new StyleColor(2, 2, 2), 3),
            scaled.Layers[3].Paint);
        var symbol = Assert.IsType<SymbolPaint>(scaled.Layers[4].Paint);
        Assert.Equal(24, symbol.Options.Size);
        Assert.Equal(4, symbol.Options.HaloWidth);
        Assert.Equal(6, symbol.Options.OffsetX);
        Assert.Equal(8, symbol.Options.OffsetY);
        Assert.Equal(10, symbol.Options.Padding);
        Assert.Equal(12, symbol.Options.IconSize);
        Assert.Equal("name", symbol.Options.TextField);
    }

    [Fact]
    public void Apply_LeavesTheCachedPlanUntouched()
    {
        var line = new LinePaint(new StyleColor(1, 2, 3), 3, [4], LineCapStyle.Butt, LineJoinStyle.Miter);
        var style = new CompiledStyle([Layer("line", line)]);

        var scaled = DpiScaling.Apply(style, 192);

        Assert.Equal(3, line.Width);
        Assert.Equal([4.0], line.Dash);
        Assert.Equal(6, Assert.IsType<LinePaint>(scaled.Layers[0].Paint).Width);
    }

    private static DrawLayer Layer(string id, PaintRecipe paint) =>
        new(id, null, DrawKind.Line, 0, 22, true, StyleFilter.Always, paint);
}
