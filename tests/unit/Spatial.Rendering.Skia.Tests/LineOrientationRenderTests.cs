using SkiaSharp;
using Spatial.Contracts;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.Operations.NetTopologySuite;
using Spatial.Rendering.Skia.Drawing;
using Spatial.Rendering.Skia.Styling;
using Spatial.Stores.Memory;
using Spatial.Transformations.ProjNet;

namespace Spatial.Rendering.Skia.Tests;

/// <summary>
/// Pins how the raster path strokes the degenerate orientations a two-point
/// segment can take (SpatialEngine-a74).
///
/// <para>
/// A two-point segment has zero area, and a vertical one also has zero extent
/// in x. Neither is a reason to drop it: the stroke still carries the line
/// weight the style gives it, so every orientation must reach the raster
/// (ADR-0044). The committed golden render (<c>symbols.png</c>) uses point
/// features only, so before these tests nothing pinned a line's orientation and
/// a vertical line could have been dropped by any layer without a test noticing.
/// </para>
///
/// <para>
/// The two genuine edge behaviours, both of which match MapLibre and are
/// pinned here rather than "fixed":
/// <list type="bullet">
/// <item>both endpoints coincident — a zero-length segment. A butt cap draws
/// nothing (correct: there is no extent to extend), while a round or square
/// cap draws the cap, i.e. a dot the line width across;</item>
/// <item>fewer than two points — a one- or zero-point LineString is not a
/// segment, so it has no cap to draw and nothing is stroked.</item>
/// </list>
/// </para>
/// </summary>
public sealed class LineOrientationRenderTests
{
    private const string LineStyle = """
        { "version": 8, "layers": [
            { "id": "rivers", "type": "line", "source-layer": "demo.cities",
              "paint": { "line-color": "#00ff00", "line-width": 6 } } ] }
        """;

    private static readonly RasterViewport Viewport = new(new Envelope(-10, -10, 10, 10), 64, 64, "EPSG:4326");

    /// <summary>Every orientation of a two-point segment, including the degenerate ones.</summary>
    public static TheoryData<string, double, double, double, double> Orientations => new()
    {
        { "horizontal", 5, -9, 9, -9 },
        { "vertical", 5, -9, 5, 9 },
        { "vertical reversed", 5, 9, 5, -9 },
        { "ascending diagonal", 5, -9, 9, 9 },
        { "descending diagonal", 5, 9, 9, -9 },
        { "horizontal at the equator", -9, 0, 9, 0 },
        { "vertical through the origin", 0, -9, 0, 9 },
        { "zero-length", 5, 5, 5, 5 },
    };

    /// <summary>Every orientation that has extent in at least one axis must stroke.</summary>
    public static TheoryData<string, double, double, double, double> StrokingOrientations => new()
    {
        { "horizontal", 5, -9, 9, -9 },
        { "vertical", 5, -9, 5, 9 },
        { "vertical reversed", 5, 9, 5, -9 },
        { "ascending diagonal", 5, -9, 9, 9 },
        { "descending diagonal", 5, 9, 9, -9 },
        { "horizontal at the equator", -9, 0, 9, 0 },
        { "vertical through the origin", 0, -9, 0, 9 },
    };

    [Theory]
    [MemberData(nameof(StrokingOrientations))]
    public async Task RenderAsync_StrokesEveryTwoPointOrientation(
        string name, double x0, double y0, double x1, double y1)
    {
        using var bitmap = await Render(x0, y0, x1, y1);
        Assert.True(CountGreen(bitmap!) > 0, $"a {name} two-point line rendered no pixels");
    }

    [Theory]
    [MemberData(nameof(StrokingOrientations))]
    public async Task RenderAsync_FromAMemoryStoreFeature_StrokesEveryTwoPointOrientation(
        string name, double x0, double y0, double x1, double y1)
    {
        using var bitmap = await RenderFromStore(x0, y0, x1, y1);
        Assert.True(CountGreen(bitmap!) > 0, $"a {name} two-point line from the store rendered no pixels");
    }

    /// <summary>
    /// The repro from the bead, verbatim: a vertical two-point feature and a
    /// single line layer over a 64×64 EPSG:4326 viewport. A vertical line is a
    /// ~58px-tall band, so it must ink far more than a horizontal one.
    /// </summary>
    [Fact]
    public async Task RenderAsync_StrokesTheBeadsVerticalRepro()
    {
        using var vertical = await Render(5, -9, 5, 9);
        using var horizontal = await Render(5, -9, 9, -9);

        Assert.True(CountGreen(vertical!) > 0, "the vertical repro rendered no pixels");
        Assert.True(CountGreen(vertical!) > CountGreen(horizontal!));
    }

    /// <summary>
    /// The stroke must be the line weight the style carries, not a hairline:
    /// 18 world units over 64 pixels is a ~58px-tall line, so a 6px stroke
    /// covers roughly a sixth of the canvas height times the canvas width.
    /// </summary>
    [Fact]
    public async Task RenderAsync_StrokesAVerticalLineAtTheStylesLineWidth()
    {
        using var bitmap = await Render(5, -9, 5, 9);

        // 18/20*64 = 57.6px tall, 6px wide, plus a 3px cap at each end.
        Assert.InRange(CountGreen(bitmap!), 300, 500);
    }

    /// <summary>
    /// A vertical line on the viewport's own x, so its zero-width envelope sits
    /// exactly on the cull boundary. The cull counts touching as inside, so it
    /// must survive; culling it would be the zero-extent bug.
    /// </summary>
    [Fact]
    public async Task RenderAsync_StrokesAVerticalLineOnTheViewportEdge()
    {
        using var bitmap = await Render(-10, -9, -10, 9);
        Assert.True(CountGreen(bitmap!) > 0, "a vertical line on the viewport edge rendered no pixels");
    }

    /// <summary>
    /// Both endpoints coincident is a zero-length segment. A butt cap has no
    /// extent to extend, so nothing is stroked — but a round or square cap
    /// draws the cap itself, a dot one line width across.
    /// </summary>
    [Theory]
    [InlineData("butt", 0)]
    [InlineData("round", 36)]
    [InlineData("square", 36)]
    public void Rasterizer_ZeroLengthSegmentDrawsOnlyItsCap(string cap, int expected)
    {
        var layer = new DrawLayer(
            "rivers", "demo", DrawKind.Line, 0, 24, true, StyleFilter.Always,
            new LinePaint(new StyleColor(0, 255, 0), 6, [], Cap(cap), LineJoinStyle.Miter));
        var geometry = GeometryFactory.CreateLineString([new Coordinate(5, 5), new Coordinate(5, 5)]);
        var scene = new RenderScene([new SceneLayer(layer, [geometry])], null);

        var buffer = SkiaVectorRasterizer.Render(scene, Viewport);

        Assert.Equal(expected, CountAlpha(buffer));
    }

    private static LineCapStyle Cap(string name) => name switch
    {
        "round" => LineCapStyle.Round,
        "square" => LineCapStyle.Square,
        _ => LineCapStyle.Butt,
    };

    /// <summary>A one- or zero-point LineString is not a segment, so it strokes nothing.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    public void Rasterizer_DrawsNothingForALineStringWithNoSegment(int coordinateCount)
    {
        var coordinates = coordinateCount == 1
            ? new[] { new Coordinate(5, 5) }
            : [];
        var layer = new DrawLayer(
            "rivers", "demo", DrawKind.Line, 0, 24, true, StyleFilter.Always,
            new LinePaint(new StyleColor(0, 255, 0), 6, [], LineCapStyle.Round, LineJoinStyle.Miter));
        var geometry = GeometryFactory.CreateLineString(coordinates);
        var scene = new RenderScene([new SceneLayer(layer, [geometry])], null);

        var buffer = SkiaVectorRasterizer.Render(scene, Viewport);

        Assert.Equal(0, CountAlpha(buffer));
    }

    /// <summary>
    /// A line is one-dimensional, so a line layer must not fill it even though
    /// its path is non-empty; and a fill layer must not stroke it either.
    /// </summary>
    [Fact]
    public void Rasterizer_KeepsLineGeometryOffTheFillPath()
    {
        var line = GeometryFactory.CreateLineString([new Coordinate(5, -9), new Coordinate(5, 9)]);
        var layer = new DrawLayer(
            "rivers", "demo", DrawKind.Fill, 0, 24, true, StyleFilter.Always,
            new FillPaint(new StyleColor(255, 0, 0), StyleColor.Transparent, 0));
        var scene = new RenderScene([new SceneLayer(layer, [line])], null);

        Assert.Equal(0, CountAlpha(SkiaVectorRasterizer.Render(scene, Viewport)));
    }

    /// <summary>
    /// The other half of the dimensional rule: a polygon is two-dimensional, so
    /// a line layer must not stroke its ring even though the ring is a closed
    /// path with plenty of extent in both axes.
    /// </summary>
    [Fact]
    public void Rasterizer_KeepsFillGeometryOffTheLinePath()
    {
        var polygon = GeometryFactory.CreatePolygon(
        [
            new Coordinate(5, -9), new Coordinate(9, -9), new Coordinate(9, 9),
            new Coordinate(5, 9), new Coordinate(5, -9)
        ]);
        var layer = new DrawLayer(
            "rivers", "demo", DrawKind.Line, 0, 24, true, StyleFilter.Always,
            new LinePaint(new StyleColor(0, 255, 0), 6, [], LineCapStyle.Round, LineJoinStyle.Miter));
        var scene = new RenderScene([new SceneLayer(layer, [polygon])], null);

        Assert.Equal(0, CountAlpha(SkiaVectorRasterizer.Render(scene, Viewport)));
    }

    /// <summary>
    /// Vertical geometry must survive the real stack, not just the fakes: the
    /// ProjNet transform and the NTS simplifier are the production engines
    /// behind placement and shaping, and a zero-width envelope is what a store
    /// bbox and a simplifier both see.
    /// </summary>
    [Theory]
    [MemberData(nameof(StrokingOrientations))]
    public async Task RenderAsync_OnTheProductionStack_StrokesEveryTwoPointOrientation(
        string name, double x0, double y0, double x1, double y1)
    {
        var store = new MemoryStore();
        var feature = TestFeatures.Line("segment", (x0, y0), (x1, y1));
        var batch = new FeatureBatch(TestFeatures.Schema, [feature]);
        await store.CreateAsync("demo.cities", batch, 4326);
        await store.WriteAsync("demo.cities", batch);
        var renderer = new MapRenderer(new ProjNetTransforms(), new NtsGeometryOperations());

        var image = await renderer.RenderAsync(new MapRenderRequest(
            Viewport, LineStyle, [new MapLayerSource("demo.cities", store, store)]));

        using var bitmap = SKBitmap.Decode(image.Content);
        Assert.True(CountGreen(bitmap!) > 0, $"a {name} two-point line on the production stack rendered no pixels");
    }

    /// <summary>
    /// The bead's original context was a data-driven line colour. Both paint
    /// paths must stroke a vertical line identically, so an expression cannot
    /// be blamed for a missing stroke and a constant paint cannot hide one.
    /// </summary>
    [Fact]
    public async Task RenderAsync_StrokesAVerticalLineOnTheDataDrivenPaintPath()
    {
        const string style = """
            { "version": 8, "layers": [
                { "id": "rivers", "type": "line", "source-layer": "demo.cities",
                  "paint": { "line-color": ["match", ["get", "name"], "segment", "#00ff00", "#ff0000"],
                             "line-width": 6 } } ] }
            """;
        var store = new FakeStore(TestFeatures.Schema, TestFeatures.Line("segment", (5, -9), (5, 9)));
        var renderer = new MapRenderer(new IdentityTransforms(), new FakeOperations());

        var image = await renderer.RenderAsync(new MapRenderRequest(
            Viewport, style, [new MapLayerSource("demo.cities", store, new FakeCatalogue(4326))]));

        using var bitmap = SKBitmap.Decode(image.Content);
        Assert.True(CountGreen(bitmap!) > 0, "a data-driven vertical line rendered no pixels");
    }

    [Fact]
    public async Task RenderAsync_HonoursCancellationForAVerticalLine()
    {
        var store = new FakeStore(TestFeatures.Schema, TestFeatures.Line("segment", (5, -9), (5, 9)));
        var renderer = new MapRenderer(new IdentityTransforms(), new FakeOperations());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => renderer.RenderAsync(Request(store), new CancellationToken(canceled: true)));
    }

    private static async Task<SKBitmap?> Render(double x0, double y0, double x1, double y1)
    {
        var store = new FakeStore(TestFeatures.Schema, TestFeatures.Line("segment", (x0, y0), (x1, y1)));
        var renderer = new MapRenderer(new IdentityTransforms(), new FakeOperations());
        return SKBitmap.Decode((await renderer.RenderAsync(Request(store))).Content);
    }

    private static async Task<SKBitmap?> RenderFromStore(double x0, double y0, double x1, double y1)
    {
        var store = new MemoryStore();
        var batch = new FeatureBatch(TestFeatures.Schema, [TestFeatures.Line("segment", (x0, y0), (x1, y1))]);
        await store.CreateAsync("demo.cities", batch, 4326);
        await store.WriteAsync("demo.cities", batch);
        var renderer = new MapRenderer(new IdentityTransforms(), new FakeOperations());
        var image = await renderer.RenderAsync(Request(store));
        return SKBitmap.Decode(image.Content);
    }

    private static MapRenderRequest Request(IFeatureStore store) => new(
        Viewport,
        LineStyle,
        [new MapLayerSource("demo.cities", store, new FakeCatalogue(4326))]);

    private static long CountGreen(SKBitmap bitmap)
    {
        long count = 0;
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.Alpha > 0 && pixel.Green > 128 && pixel.Red < 128)
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static int CountAlpha(RasterBuffer buffer)
    {
        int count = 0;
        for (var y = 0; y < buffer.Height; y++)
        {
            for (var x = 0; x < buffer.Width; x++)
            {
                if (buffer.Pixels.Span[(y * buffer.Stride) + (x * 4) + 3] > 0)
                {
                    count++;
                }
            }
        }

        return count;
    }
}
