using Spatial.Contracts;
using Spatial.Core.Geometry;
using Spatial.Rendering.Skia.Drawing;
using Spatial.Rendering.Skia.Styling;

namespace Spatial.Rendering.Skia.Tests;

public sealed class SymbolRasterizerTests
{
    private const string MarkerSvg =
        """<svg xmlns="http://www.w3.org/2000/svg" width="20" height="20" viewBox="0 0 20 20"><rect width="20" height="20" fill="#ff0000"/></svg>""";

    private static readonly RasterViewport Viewport = new(new Envelope(0, 0, 10, 10), 100, 100, "EPSG:4326");

    [Fact]
    public void Render_DrawsATextLabelAtItsAnchor()
    {
        var buffer = Render(Options("Hello"), Feature("Hello", 5, 5));

        Assert.True(Ink(buffer, 30, 35, 70, 65) > 0);
    }

    [Fact]
    public void Render_TextAnchorMovesTheLabelRelativeToThePoint()
    {
        var left = Render(Options("Hi", anchor: SymbolAnchor.Left), Feature("Hi", 5, 5));
        var right = Render(Options("Hi", anchor: SymbolAnchor.Right), Feature("Hi", 5, 5));

        Assert.True(Ink(left, 52, 35, 100, 65) > Ink(left, 0, 35, 48, 65));
        Assert.True(Ink(right, 0, 35, 48, 65) > Ink(right, 52, 35, 100, 65));
    }

    [Fact]
    public void Render_TextOffsetShiftsTheLabel()
    {
        var centred = Render(Options("Hi"), Feature("Hi", 5, 5));
        var shifted = Render(Options("Hi", offsetX: 1.5), Feature("Hi", 5, 5));

        Assert.True(Ink(shifted, 85, 35, 100, 65) > Ink(centred, 85, 35, 100, 65));
    }

    [Fact]
    public void Render_TextHaloDrawsAroundTheGlyphs()
    {
        var plain = Render(Options("Halo", color: new StyleColor(0, 0, 0)), Feature("Halo", 5, 5));
        var haloed = Render(
            Options("Halo", color: new StyleColor(0, 0, 0), haloColor: new StyleColor(255, 0, 0), haloWidth: 3),
            Feature("Halo", 5, 5));

        Assert.True(Ink(haloed, 20, 30, 80, 70) > Ink(plain, 20, 30, 80, 70));
    }

    [Fact]
    public void Render_CollisionKeepsTheFirstLabelAndSkipsTheSecond()
    {
        var options = Options("Label");
        var first = Render(options, Feature("Alpha", 5, 5));
        var both = Render(options, Feature("Alpha", 5, 5), Feature("Beta", 5.2, 5.2));

        Assert.Equal(first.Pixels.ToArray(), both.Pixels.ToArray());
    }

    [Fact]
    public void Render_AllowOverlapPlacesEveryLabel()
    {
        var options = Options("Label", allowOverlap: true);
        var first = Render(options, Feature("Alpha", 5, 5));
        var both = Render(options, Feature("Alpha", 5, 5), Feature("Beta", 5.2, 5.2));

        Assert.NotEqual(first.Pixels.ToArray(), both.Pixels.ToArray());
    }

    [Fact]
    public void Render_RepeatedRunsAreByteIdentical()
    {
        var options = Options("Deterministic");
        var scene = Scene(options, Feature("Alpha", 4, 5), Feature("Beta", 6, 5));

        Assert.Equal(
            SkiaVectorRasterizer.Render(scene, Viewport).Pixels.ToArray(),
            SkiaVectorRasterizer.Render(scene, Viewport).Pixels.ToArray());
    }

    [Fact]
    public void Render_DrawsAnEmbeddedSpriteIcon()
    {
        var sprites = SpriteRegistry.FromSources([new("marker", MarkerSvg)]);
        var scene = Scene(Options("", icon: "marker", allowIconOverlap: true), Feature(null, 5, 5, "marker"));

        var buffer = SkiaVectorRasterizer.Render(scene, Viewport, sprites);

        Assert.True(Ink(buffer, 40, 40, 60, 60) > 0);
    }

    [Fact]
    public void Render_RejectsAnUnknownIconWithATypedError()
    {
        var sprites = SpriteRegistry.FromSources([new("marker", MarkerSvg)]);
        var scene = Scene(Options("", icon: "absent", allowIconOverlap: true), Feature(null, 5, 5, "absent"));

        var exception = Assert.Throws<SpatialException>(() => SkiaVectorRasterizer.Render(scene, Viewport, sprites));
        Assert.Equal(SpatialException.InvalidArguments, exception.Code);
    }

    [Fact]
    public void Render_AnchorsPolygonLabelsAtTheEnvelopeCentre()
    {
        var geometry = GeometryFactory.CreatePolygon(
        [
            new Coordinate(6, 6),
            new Coordinate(8, 6),
            new Coordinate(8, 8),
            new Coordinate(6, 8),
            new Coordinate(6, 6),
        ]);
        var scene = Scene(Options("Polygon"), new SymbolFeature(geometry, "Polygon", null));

        Assert.True(Ink(SkiaVectorRasterizer.Render(scene, Viewport), 60, 10, 100, 50) > 0);
    }

    private static SymbolOptions Options(
        string textField,
        double size = 20,
        StyleColor? color = null,
        StyleColor? haloColor = null,
        double haloWidth = 0,
        SymbolAnchor anchor = SymbolAnchor.Center,
        double offsetX = 0,
        bool allowOverlap = false,
        string? icon = null,
        bool allowIconOverlap = false) =>
        new(
            textField,
            [],
            size,
            color ?? new StyleColor(0, 0, 0),
            haloColor ?? StyleColor.Transparent,
            haloWidth,
            anchor,
            offsetX,
            0,
            2,
            allowOverlap,
            icon,
            1,
            allowIconOverlap);

    private static RasterBuffer Render(SymbolOptions options, params SymbolFeature[] features) =>
        SkiaVectorRasterizer.Render(Scene(options, features), Viewport);

    private static RenderScene Scene(SymbolOptions options, params SymbolFeature[] features)
    {
        var layer = new DrawLayer("symbols", "demo", DrawKind.Symbol, 0, 24, true, StyleFilter.Always, new SymbolPaint(options));
        return new RenderScene(
            [new SceneLayer(layer, []) { Symbols = features }],
            new StyleColor(255, 255, 255));
    }

    private static SymbolFeature Feature(string? text, double x, double y, string? icon = null) =>
        new(GeometryFactory.CreatePoint(x, y), text, icon);

    private static long Ink(RasterBuffer buffer, int minX, int minY, int maxX, int maxY)
    {
        long count = 0;
        var span = buffer.Pixels.Span;
        for (var y = minY; y < maxY; y++)
        {
            for (var x = minX; x < maxX; x++)
            {
                var offset = y * buffer.Stride + x * 4;
                if (span[offset] < 200 || span[offset + 1] < 200 || span[offset + 2] < 200)
                {
                    count++;
                }
            }
        }

        return count;
    }
}
