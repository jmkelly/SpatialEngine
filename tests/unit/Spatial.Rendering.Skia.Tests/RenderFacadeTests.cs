using SkiaSharp;
using Spatial.Core.Geometry;
using Spatial.PluginSdk;

namespace Spatial.Rendering.Skia.Tests;

public sealed class RenderFacadeTests
{
    private const string CircleStyle = """
        { "version": 8, "layers": [
            { "id": "cities", "type": "circle", "source-layer": "demo.cities",
              "paint": { "circle-color": "#ffd166", "circle-radius": 4 } } ] }
        """;

    private const string ZoomedStyle = """
        { "version": 8, "layers": [
            { "id": "cities", "type": "circle", "source-layer": "demo.cities", "minzoom": 12,
              "paint": { "circle-color": "#ffd166", "circle-radius": 4 } } ] }
        """;

    [Fact]
    public async Task RenderAsync_ProducesAnImageThroughTheVectorOnlyPath()
    {
        var store = new FakeStore(TestFeatures.Schema, TestFeatures.Point("London", 0, 0));
        var renderer = new MapRenderer(new IdentityTransforms(), new FakeOperations());

        var image = await renderer.RenderAsync(Request(store));

        Assert.Equal(RasterFormat.Png, image.Format);
        Assert.Equal(100, image.Width);
        Assert.Equal(100, image.Height);
        Assert.Equal(0x89, image.Content[0]);
        Assert.Equal(new BoundingBox(-10, -10, 10, 10), store.LastBbox);
    }

    [Fact]
    public async Task RenderAsync_PassesTheStoreLevelFilterThrough()
    {
        var store = new FakeStore(TestFeatures.Schema, TestFeatures.Point("London", 0, 0));
        var renderer = new MapRenderer(new IdentityTransforms(), new FakeOperations());
        var request = Request(store) with
        {
            Layers = [new MapLayerSource("demo.cities", store, new FakeCatalogue(4326), "name='London'")],
        };

        await renderer.RenderAsync(request);

        Assert.Equal("name='London'", store.LastFilter);
    }

    [Fact]
    public async Task RenderAsync_ComposesImageryOverTheVectorBuffer()
    {
        var store = new FakeStore(TestFeatures.Schema, TestFeatures.Point("London", 0, 0));
        var imagery = new FakeRasterOperations();
        var renderer = new MapRenderer(new IdentityTransforms(), new FakeOperations(), imagery);
        var request = Request(store) with { Imagery = [new RasterSourceLayer("basemap")] };

        var image = await renderer.RenderAsync(request);

        Assert.Equal([9, 8, 7], image.Content);
        Assert.NotNull(imagery.LastComposite);
        Assert.Equal(2, imagery.LastComposite!.Layers.Count);
        Assert.IsType<RasterSourceLayer>(imagery.LastComposite.Layers[0]);
        Assert.IsType<RasterBufferLayer>(imagery.LastComposite.Layers[1]);
    }

    [Fact]
    public async Task RenderAsync_RejectsImageryWithoutAnImageryPipeline()
    {
        var store = new FakeStore(TestFeatures.Schema, TestFeatures.Point("London", 0, 0));
        var renderer = new MapRenderer(new IdentityTransforms(), new FakeOperations());
        var request = Request(store) with { Imagery = [new RasterSourceLayer("basemap")] };

        var exception = await Assert.ThrowsAsync<SpatialException>(() => renderer.RenderAsync(request));
        Assert.Equal(SpatialException.InvalidArguments, exception.Code);
    }

    [Fact]
    public async Task RenderAsync_AppliesTheDevicePixelRatio()
    {
        var store = new FakeStore(TestFeatures.Schema, TestFeatures.Point("London", 0, 0));
        var renderer = new MapRenderer(new IdentityTransforms(), new FakeOperations());
        var request = Request(store) with { Scale = 2 };

        var image = await renderer.RenderAsync(request);

        Assert.Equal(200, image.Width);
        Assert.Equal(200, image.Height);
    }

    [Fact]
    public async Task RenderAsync_HidesLayersOutsideTheirZoomWindow()
    {
        var store = new FakeStore(TestFeatures.Schema, TestFeatures.Point("London", 0, 0));
        var renderer = new MapRenderer(new IdentityTransforms(), new FakeOperations());
        var request = Request(store) with { Style = ZoomedStyle };

        await renderer.RenderAsync(request);

        Assert.Null(store.LastBbox);
    }

    [Theory]
    [InlineData("EPSG:3857")]
    [InlineData("EPSG:32631")]
    public async Task RenderAsync_ComputesZoomForKnownAndUnknownCrs(string crs)
    {
        var store = new FakeStore(TestFeatures.Schema, TestFeatures.Point("London", 0, 0));
        var renderer = new MapRenderer(new IdentityTransforms(), new FakeOperations());
        var request = Request(store) with
        {
            Viewport = new RasterViewport(new Envelope(0, 0, 1000, 1000), 100, 100, crs),
        };

        var image = await renderer.RenderAsync(request);

        Assert.Equal(100, image.Width);
    }

    [Fact]
    public async Task RenderAsync_RejectsAViewportAboveThePixelCap()
    {
        var store = new FakeStore(TestFeatures.Schema, TestFeatures.Point("London", 0, 0));
        var renderer = new MapRenderer(new IdentityTransforms(), new FakeOperations(), limits: new RenderLimits(10, 4));

        var exception = await Assert.ThrowsAsync<SpatialException>(() => renderer.RenderAsync(Request(store)));
        Assert.Equal(SpatialException.InvalidArguments, exception.Code);
    }

    [Fact]
    public async Task RenderAsync_RejectsTooManyLayers()
    {
        var store = new FakeStore(TestFeatures.Schema, TestFeatures.Point("London", 0, 0));
        var renderer = new MapRenderer(new IdentityTransforms(), new FakeOperations(), limits: new RenderLimits(1_000_000, 1));
        var request = Request(store) with
        {
            Layers =
            [
                new MapLayerSource("demo.cities", store, new FakeCatalogue(4326)),
                new MapLayerSource("demo.other", store, new FakeCatalogue(4326)),
            ],
        };

        var exception = await Assert.ThrowsAsync<SpatialException>(() => renderer.RenderAsync(request));
        Assert.Equal(SpatialException.InvalidArguments, exception.Code);
    }

    [Fact]
    public async Task RenderAsync_RejectsADatasetMissingFromTheRequest()
    {
        var store = new FakeStore(TestFeatures.Schema, TestFeatures.Point("London", 0, 0));
        var renderer = new MapRenderer(new IdentityTransforms(), new FakeOperations());
        var request = Request(store) with
        {
            Style = CircleStyle.Replace("demo.cities", "demo.other", StringComparison.Ordinal),
        };

        var exception = await Assert.ThrowsAsync<SpatialException>(() => renderer.RenderAsync(request));
        Assert.Equal(SpatialException.InvalidArguments, exception.Code);
    }

    [Fact]
    public async Task RenderAsync_RejectsADuplicateDataset()
    {
        var store = new FakeStore(TestFeatures.Schema, TestFeatures.Point("London", 0, 0));
        var catalogue = new FakeCatalogue(4326);
        var renderer = new MapRenderer(new IdentityTransforms(), new FakeOperations());
        var request = Request(store) with
        {
            Layers =
            [
                new MapLayerSource("demo.cities", store, catalogue),
                new MapLayerSource("demo.cities", store, catalogue),
            ],
        };

        await Assert.ThrowsAsync<SpatialException>(() => renderer.RenderAsync(request));
    }

    [Fact]
    public async Task RenderAsync_RejectsANonPositiveScale()
    {
        var store = new FakeStore(TestFeatures.Schema, TestFeatures.Point("London", 0, 0));
        var renderer = new MapRenderer(new IdentityTransforms(), new FakeOperations());

        await Assert.ThrowsAsync<SpatialException>(() => renderer.RenderAsync(Request(store) with { Scale = 0 }));
    }

    [Fact]
    public async Task RenderAsync_RejectsAnEmptyViewport()
    {
        var store = new FakeStore(TestFeatures.Schema, TestFeatures.Point("London", 0, 0));
        var renderer = new MapRenderer(new IdentityTransforms(), new FakeOperations());
        var request = Request(store) with
        {
            Viewport = new RasterViewport(new Envelope(0, 0, 0, 0), 0, 0, "EPSG:4326"),
        };

        await Assert.ThrowsAsync<SpatialException>(() => renderer.RenderAsync(request));
    }

    [Fact]
    public async Task RenderAsync_RejectsAnUnknownLayerType()
    {
        var store = new FakeStore(TestFeatures.Schema, TestFeatures.Point("London", 0, 0));
        var renderer = new MapRenderer(new IdentityTransforms(), new FakeOperations());
        var request = Request(store) with { Style = "{ \"version\": 8, \"layers\": [ { \"id\": \"x\", \"type\": \"symbol\" } ] }" };

        await Assert.ThrowsAsync<SpatialException>(() => renderer.RenderAsync(request));
    }

    [Fact]
    public async Task RenderAsync_HonoursCancellation()
    {
        var store = new FakeStore(TestFeatures.Schema, TestFeatures.Point("London", 0, 0));
        var renderer = new MapRenderer(new IdentityTransforms(), new FakeOperations());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => renderer.RenderAsync(Request(store), new CancellationToken(canceled: true)));
    }

    [Fact]
    public async Task RenderAsync_BakesTheRequestBackgroundWhenThereIsNoImagery()
    {
        var store = new FakeStore(TestFeatures.Schema, TestFeatures.Point("London", 0, 0));
        var renderer = new MapRenderer(new IdentityTransforms(), new FakeOperations());
        var request = Request(store) with { Background = "#0000ff", Transparent = false };

        var image = await renderer.RenderAsync(request);

        using var bitmap = SKBitmap.Decode(image.Content);
        Assert.NotNull(bitmap);
        var pixel = bitmap.GetPixel(5, 5);
        Assert.Equal((byte)0, pixel.Red);
        Assert.Equal((byte)0, pixel.Green);
        Assert.Equal((byte)255, pixel.Blue);
        Assert.Equal((byte)255, pixel.Alpha);
    }

    private static MapRenderRequest Request(IFeatureStore store) => new(
        new RasterViewport(new Envelope(-10, -10, 10, 10), 100, 100, "EPSG:4326"),
        CircleStyle,
        [new MapLayerSource("demo.cities", store, new FakeCatalogue(4326))]);
}
