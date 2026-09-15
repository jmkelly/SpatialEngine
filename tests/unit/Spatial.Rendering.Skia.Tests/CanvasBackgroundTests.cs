using Spatial.Contracts;
using Spatial.Core.Geometry;
using Spatial.Rendering.Skia.Drawing;
using Spatial.Rendering.Skia.Styling;

namespace Spatial.Rendering.Skia.Tests;

public sealed class CanvasBackgroundTests
{
    [Fact]
    public void Apply_KeepsAStyleBackground()
    {
        var result = CanvasBackground.Apply(new RenderScene([], new StyleColor(1, 2, 3)), Request("#ffffff"));

        Assert.Equal(new StyleColor(1, 2, 3), result.Background);
    }

    [Fact]
    public void Apply_LeavesTheVectorBufferTransparentWhenImageryIsRequested()
    {
        var request = Request("#ffffff") with { Imagery = [new RasterSourceLayer("basemap")] };

        Assert.Null(CanvasBackground.Apply(new RenderScene([], null), request).Background);
    }

    [Fact]
    public void Apply_UsesTheRequestBackgroundWithoutImagery()
    {
        var result = CanvasBackground.Apply(new RenderScene([], null), Request("#0000ff"));

        Assert.Equal(new StyleColor(0, 0, 255), result.Background);
    }

    [Fact]
    public void Apply_DefaultsToOpaqueWhiteWhenNotTransparent()
    {
        var request = Request(null) with { Transparent = false };

        Assert.Equal(new StyleColor(255, 255, 255), CanvasBackground.Apply(new RenderScene([], null), request).Background);
    }

    [Fact]
    public void Apply_LeavesTransparentWhenAsked()
    {
        Assert.Null(CanvasBackground.Apply(new RenderScene([], null), Request(null)).Background);
    }

    [Fact]
    public void Apply_RejectsAnInvalidBackground()
    {
        Assert.Throws<SpatialException>(() => CanvasBackground.Apply(new RenderScene([], null), Request("mauve")));
    }

    [Fact]
    public void StyleColorParser_Parse_ThrowsForUnsupportedColours()
    {
        Assert.Equal(new StyleColor(0, 0, 255), StyleColorParser.Parse("#00f"));
        Assert.Equal(SpatialException.InvalidArguments, Assert.Throws<SpatialException>(() => StyleColorParser.Parse("mauve")).Code);
    }

    private static MapRenderRequest Request(string? background) => new(
        new RasterViewport(new Envelope(0, 0, 1, 1), 10, 10, "EPSG:4326"),
        "{}",
        [],
        Background: background);
}
