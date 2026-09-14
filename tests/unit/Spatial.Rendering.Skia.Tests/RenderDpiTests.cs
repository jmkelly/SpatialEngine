using SkiaSharp;
using Spatial.Core.Geometry;
using Spatial.PluginSdk;

namespace Spatial.Rendering.Skia.Tests;

/// <summary>
/// Scale-aware DPI (T-045 item 3): style pixel sizes are defined at 96 dpi,
/// so a higher request DPI enlarges symbology while the output frame keeps
/// the requested WIDTHxHEIGHT. The 96-dpi default renders exactly as before.
/// </summary>
public sealed class RenderDpiTests
{
    private const string CircleStyle = """
        { "version": 8, "layers": [
            { "id": "cities", "type": "circle", "source-layer": "demo.cities",
              "paint": { "circle-color": "#ffd166", "circle-radius": 4 } } ] }
        """;

    [Fact]
    public async Task RenderAsync_At96DpiMatchesTheDefaultRequest()
    {
        var store = new FakeStore(TestFeatures.Schema, TestFeatures.Point("London", 0, 0));
        var renderer = new MapRenderer(new IdentityTransforms(), new FakeOperations());

        var baseline = await renderer.RenderAsync(Request(store));
        var explicitDpi = await renderer.RenderAsync(Request(store) with { Dpi = 96 });

        Assert.Equal(baseline.Content, explicitDpi.Content);
    }

    [Fact]
    public async Task RenderAsync_ScalesSymbologyWithDpiButKeepsTheFrame()
    {
        var store = new FakeStore(TestFeatures.Schema, TestFeatures.Point("London", 0, 0));
        var renderer = new MapRenderer(new IdentityTransforms(), new FakeOperations());

        var normal = await renderer.RenderAsync(Request(store));
        var dense = await renderer.RenderAsync(Request(store) with { Dpi = 192 });

        Assert.Equal(normal.Width, dense.Width);
        Assert.Equal(normal.Height, dense.Height);
        Assert.NotEqual(normal.Content, dense.Content);
        Assert.True(CountInked(dense) > CountInked(normal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-72)]
    public async Task RenderAsync_RejectsANonPositiveDpi(double dpi)
    {
        var store = new FakeStore(TestFeatures.Schema, TestFeatures.Point("London", 0, 0));
        var renderer = new MapRenderer(new IdentityTransforms(), new FakeOperations());

        var exception = await Assert.ThrowsAsync<SpatialException>(
            () => renderer.RenderAsync(Request(store) with { Dpi = dpi }));

        Assert.Equal(SpatialException.InvalidArguments, exception.Code);
    }

    [Fact]
    public async Task RenderAsync_RejectsANonFiniteDpi()
    {
        var store = new FakeStore(TestFeatures.Schema, TestFeatures.Point("London", 0, 0));
        var renderer = new MapRenderer(new IdentityTransforms(), new FakeOperations());

        var exception = await Assert.ThrowsAsync<SpatialException>(
            () => renderer.RenderAsync(Request(store) with { Dpi = double.NaN }));

        Assert.Equal(SpatialException.InvalidArguments, exception.Code);
    }

    [Fact]
    public async Task RenderAsync_HonoursCancellationWithAnExplicitDpi()
    {
        var store = new FakeStore(TestFeatures.Schema, TestFeatures.Point("London", 0, 0));
        var renderer = new MapRenderer(new IdentityTransforms(), new FakeOperations());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => renderer.RenderAsync(
                Request(store) with { Dpi = 192 }, new CancellationToken(canceled: true)));
    }

    private static MapRenderRequest Request(IFeatureStore store) => new(
        new RasterViewport(new Envelope(-10, -10, 10, 10), 100, 100, "EPSG:4326"),
        CircleStyle,
        [new MapLayerSource("demo.cities", store, new FakeCatalogue(4326))]);

    private static long CountInked(RasterImage image)
    {
        using var bitmap = SKBitmap.Decode(image.Content);
        Assert.NotNull(bitmap);
        long count = 0;
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).Alpha > 0)
                {
                    count++;
                }
            }
        }

        return count;
    }
}
