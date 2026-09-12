using NetVips;
using Spatial.Imagery.Vips.Imagery;
using Spatial.PluginSdk;

namespace Spatial.Imagery.Vips.Tests;

public sealed class VipsCompositorTests
{
    private static readonly RasterViewport Viewport = new(new Spatial.Core.Geometry.Envelope(0, 0, 1, 1), 4, 4, "EPSG:4326");

    [Fact]
    public void Compose_RequiresALayerUnlessThereIsABackground()
    {
        Assert.Equal(
            SpatialException.InvalidArguments,
            Assert.Throws<SpatialException>(() => VipsCompositor.Compose(Viewport, [], Sources(), null, true)).Code);
    }

    [Fact]
    public void Compose_RejectsAnInvalidViewport()
    {
        var invalid = new RasterViewport(new Spatial.Core.Geometry.Envelope(0, 0, 0, 0), 0, 0, "EPSG:4326");
        Assert.Equal(
            SpatialException.InvalidArguments,
            Assert.Throws<SpatialException>(() => VipsCompositor.Compose(invalid, [], Sources(), "#ffffff", true)).Code);
    }

    [Fact]
    public void Compose_BuildsABackgroundWithoutLayers()
    {
        using var composition = VipsCompositor.Compose(Viewport, [], Sources(), "#102030", true);
        Assert.Equal(4, composition.Result.Width);
        Assert.Equal(4, composition.Result.Height);
        Assert.Equal(4, composition.Result.Bands);
    }

    [Fact]
    public void Compose_StacksBuffersAndImagery()
    {
        using var imagery = new ImageryFixture();
        var layers = new RasterLayer[]
        {
            new RasterSourceLayer("basemap"),
            new RasterBufferLayer(Buffer(255, 0, 0, 128)),
        };

        using var composition = VipsCompositor.Compose(Viewport, layers, imagery.Sources, null, true);
        Assert.Equal(4, composition.Result.Bands);
        Assert.Equal(Enums.Interpretation.Srgb, composition.Result.Interpretation);
    }

    [Fact]
    public void Compose_RejectsABufferThatDoesNotMatchTheViewport()
    {
        var layers = new RasterLayer[] { new RasterBufferLayer(Buffer(255, 0, 0, 255, width: 2, height: 2)) };
        Assert.Equal(
            SpatialException.InvalidArguments,
            Assert.Throws<SpatialException>(() => VipsCompositor.Compose(Viewport, layers, Sources(), null, true)).Code);
    }

    [Fact]
    public void Compose_RejectsUnknownLayers()
    {
        var layers = new RasterLayer[] { new UnknownLayer() };
        Assert.Equal(
            SpatialException.InvalidArguments,
            Assert.Throws<SpatialException>(() => VipsCompositor.Compose(Viewport, layers, Sources(), null, true)).Code);
    }

    internal static RasterBuffer Buffer(byte red, byte green, byte blue, byte alpha, int width = 4, int height = 4)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = red;
            pixels[i + 1] = green;
            pixels[i + 2] = blue;
            pixels[i + 3] = alpha;
        }

        return new RasterBuffer(pixels, width, height, width * 4, RasterPixelFormat.Rgba8888, Premultiplied: false);
    }

    private static Dictionary<string, string> Sources() => new(StringComparer.Ordinal);

    private sealed record UnknownLayer : RasterLayer;
}

public sealed class VipsRasterOperationsTests
{
    private static readonly RasterViewport Viewport = new(new Spatial.Core.Geometry.Envelope(0, 0, 1, 1), 4, 4, "EPSG:4326");

    [Fact]
    public async Task ReadAsync_ReturnsAnEncodedImage()
    {
        using var fixture = new ImageryFixture();
        var operations = new VipsRasterOperations(fixture.Sources);

        var image = await operations.ReadAsync(new RasterReadRequest("basemap", Viewport));

        Assert.Equal(RasterFormat.Png, image.Format);
        Assert.Equal(4, image.Width);
        Assert.Equal(4, image.Height);
        Assert.NotEmpty(image.Content);
    }

    [Fact]
    public async Task ReadAsync_RejectsAnInvalidViewport()
    {
        using var fixture = new ImageryFixture();
        var operations = new VipsRasterOperations(fixture.Sources);
        var invalid = new RasterViewport(new Spatial.Core.Geometry.Envelope(0, 0, 0, 0), 0, 0, "EPSG:4326");

        await Assert.ThrowsAsync<SpatialException>(() => operations.ReadAsync(new RasterReadRequest("basemap", invalid)));
    }

    [Fact]
    public async Task CompositeAsync_BlendsABufferStack()
    {
        var operations = new VipsRasterOperations(new Dictionary<string, string>(StringComparer.Ordinal));
        var request = new RasterCompositeRequest(
            Viewport,
            [new RasterBufferLayer(VipsCompositorTests.Buffer(255, 0, 0, 128)), new RasterBufferLayer(VipsCompositorTests.Buffer(0, 0, 255, 255))],
            RasterFormat.Jpeg,
            80);

        var image = await operations.CompositeAsync(request);

        Assert.Equal(RasterFormat.Jpeg, image.Format);
        Assert.Equal(4, image.Width);
        Assert.NotEmpty(image.Content);
    }

    [Fact]
    public async Task CompositeAsync_RejectsAnEmptyTransparentStack()
    {
        var operations = new VipsRasterOperations(new Dictionary<string, string>(StringComparer.Ordinal));
        await Assert.ThrowsAsync<SpatialException>(
            () => operations.CompositeAsync(new RasterCompositeRequest(Viewport, [])));
    }

    [Fact]
    public async Task ReadAsync_HonoursCancellation()
    {
        using var fixture = new ImageryFixture();
        var operations = new VipsRasterOperations(fixture.Sources);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => operations.ReadAsync(new RasterReadRequest("basemap", Viewport), new CancellationToken(canceled: true)));
    }
}
