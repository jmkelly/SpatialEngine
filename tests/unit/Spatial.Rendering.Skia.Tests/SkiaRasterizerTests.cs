using Spatial.Contracts;
using Spatial.Core.Geometry;
using Spatial.Rendering.Skia.Drawing;
using Spatial.Rendering.Skia.Styling;

namespace Spatial.Rendering.Skia.Tests;

public sealed class SkiaRasterizerTests
{
    private static readonly RasterViewport Viewport = new(new Envelope(0, 0, 10, 10), 10, 10, "EPSG:4326");

    [Fact]
    public void Render_FillsBackgroundWhenConfigured()
    {
        var buffer = SkiaVectorRasterizer.Render(new RenderScene([], new StyleColor(0, 0, 255)), Viewport);
        var pixel = Pixel(buffer, 5, 5);
        Assert.Equal((byte)0, pixel.R);
        Assert.Equal((byte)0, pixel.G);
        Assert.Equal((byte)255, pixel.B);
        Assert.Equal((byte)255, pixel.A);
    }

    [Fact]
    public void Render_LeavesCanvasTransparentWithoutBackground()
    {
        var buffer = SkiaVectorRasterizer.Render(new RenderScene([], null), Viewport);
        Assert.Equal((byte)0, Pixel(buffer, 5, 5).A);
    }

    [Fact]
    public void Render_DrawsAFill()
    {
        var geometry = GeometryFactory.CreatePolygon(
        [
            new Coordinate(0, 0),
            new Coordinate(10, 0),
            new Coordinate(10, 10),
            new Coordinate(0, 10),
            new Coordinate(0, 0),
        ]);
        var scene = new RenderScene(
            [new SceneLayer(Layer(new FillPaint(new StyleColor(255, 0, 0), StyleColor.Transparent, 0)), [geometry])],
            null);

        var pixel = Pixel(SkiaVectorRasterizer.Render(scene, Viewport), 5, 5);
        Assert.True(pixel.R > 240);
        Assert.True(pixel.G < 16);
        Assert.True(pixel.A > 240);
    }

    [Fact]
    public void Render_DrawsACircle()
    {
        var scene = new RenderScene(
            [new SceneLayer(Layer(new CirclePaint(new StyleColor(0, 255, 0), 3, StyleColor.Transparent, 0)), [GeometryFactory.CreatePoint(5, 5)])],
            null);

        var pixel = Pixel(SkiaVectorRasterizer.Render(scene, Viewport), 5, 5);
        Assert.True(pixel.G > 240);
        Assert.True(pixel.A > 240);
        Assert.Equal((byte)0, Pixel(SkiaVectorRasterizer.Render(scene, Viewport), 0, 0).A);
    }

    [Fact]
    public void Render_DrawsALine()
    {
        var geometry = GeometryFactory.CreateLineString([new Coordinate(0, 5), new Coordinate(10, 5)]);
        var scene = new RenderScene(
            [new SceneLayer(Layer(new LinePaint(new StyleColor(0, 0, 0), 4, [], LineCapStyle.Butt, LineJoinStyle.Miter)), [geometry])],
            null);

        Assert.True(Pixel(SkiaVectorRasterizer.Render(scene, Viewport), 5, 5).A > 240);
    }

    [Fact]
    public void Render_SkipsEmptyPathsAndBackgroundLayers()
    {
        var scene = new RenderScene(
            [
                new SceneLayer(Layer(new BackgroundPaint(new StyleColor(1, 2, 3))), []),
                new SceneLayer(Layer(new FillPaint(new StyleColor(255, 0, 0), StyleColor.Transparent, 0)), []),
            ],
            null);

        Assert.Equal((byte)0, Pixel(SkiaVectorRasterizer.Render(scene, Viewport), 5, 5).A);
    }

    [Fact]
    public void Render_DrawsMultiAndCollectionGeometries()
    {
        var fill = new FillPaint(new StyleColor(255, 0, 0), StyleColor.Transparent, 0);
        var stroke = new LinePaint(new StyleColor(0, 0, 0), 4, [], LineCapStyle.Butt, LineJoinStyle.Miter);
        var circle = new CirclePaint(new StyleColor(0, 255, 0), 3, StyleColor.Transparent, 0);

        Assert.True(Covered(
            GeometryFactory.CreateMultiPolygon(Box(0, 0, 10, 10), Box(20, 20, 30, 30)), fill) > 240);
        Assert.True(Covered(
            GeometryFactory.CreateGeometryCollection(Box(0, 0, 10, 10)), fill) > 240);
        Assert.True(Covered(
            GeometryFactory.CreateMultiLineString(Horizontal(5), Horizontal(8)), stroke) > 240);
        Assert.True(Covered(
            GeometryFactory.CreateGeometryCollection(Horizontal(5)), stroke) > 240);
        Assert.True(Covered(
            GeometryFactory.CreateMultiPoint(GeometryFactory.CreatePoint(5, 5), GeometryFactory.CreatePoint(8, 8)), circle) > 240);
        Assert.True(Covered(
            GeometryFactory.CreateGeometryCollection(GeometryFactory.CreatePoint(5, 5)), circle) > 240);
    }

    private static DrawLayer Layer(PaintRecipe paint) =>
        new("layer", "demo", DrawKind.Fill, 0, 24, true, StyleFilter.Always, paint);

    private static byte Covered(IGeometry geometry, PaintRecipe paint) =>
        Pixel(SkiaVectorRasterizer.Render(new RenderScene([new SceneLayer(Layer(paint), [geometry])], null), Viewport), 5, 5).A;

    private static Polygon Box(double minX, double minY, double maxX, double maxY) =>
        GeometryFactory.CreatePolygon(
        [
            new Coordinate(minX, minY),
            new Coordinate(maxX, minY),
            new Coordinate(maxX, maxY),
            new Coordinate(minX, maxY),
            new Coordinate(minX, minY),
        ]);

    private static LineString Horizontal(double y) =>
        GeometryFactory.CreateLineString([new Coordinate(0, y), new Coordinate(10, y)]);

    private static (byte R, byte G, byte B, byte A) Pixel(RasterBuffer buffer, int x, int y)
    {
        var span = buffer.Pixels.Span;
        var offset = y * buffer.Stride + x * 4;
        return (span[offset], span[offset + 1], span[offset + 2], span[offset + 3]);
    }
}
