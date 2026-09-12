using Spatial.Core.Geometry;
using Spatial.PluginSdk;
using Spatial.Rendering.Skia.Drawing;
using Spatial.Rendering.Skia.Styling;

namespace Spatial.Rendering.Skia.Tests;

public sealed class SkiaImageEncoderTests
{
    private static readonly RasterBuffer Buffer = SkiaVectorRasterizer.Render(
        new RenderScene([], new StyleColor(204, 51, 17)), new RasterViewport(new Envelope(0, 0, 1, 1), 4, 4, "EPSG:4326"));

    [Fact]
    public void Encode_ProducesPngBytes()
    {
        var image = SkiaImageEncoder.Encode(Buffer, RasterFormat.Png, 90);
        Assert.Equal("image/png", image.MediaType);
        Assert.Equal(RasterFormat.Png, image.Format);
        Assert.Equal(4, image.Width);
        Assert.Equal(0x89, image.Content[0]);
    }

    [Fact]
    public void Encode_ProducesJpegBytes()
    {
        var image = SkiaImageEncoder.Encode(Buffer, RasterFormat.Jpeg, 80);
        Assert.Equal("image/jpeg", image.MediaType);
        Assert.Equal(0xFF, image.Content[0]);
        Assert.Equal(0xD8, image.Content[1]);
    }

    [Fact]
    public void Encode_ProducesWebpBytes()
    {
        var image = SkiaImageEncoder.Encode(Buffer, RasterFormat.Webp, 80);
        Assert.Equal("image/webp", image.MediaType);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(image.Content, 0, 4));
    }

    [Fact]
    public void Encode_RejectsTiffWithoutImagery()
    {
        var exception = Assert.Throws<SpatialException>(() => SkiaImageEncoder.Encode(Buffer, RasterFormat.Tiff, 90));
        Assert.Equal(SpatialException.InvalidArguments, exception.Code);
    }

    [Fact]
    public void Supports_OnlyTheVectorOnlyFormats()
    {
        Assert.True(SkiaImageEncoder.Supports(RasterFormat.Png));
        Assert.True(SkiaImageEncoder.Supports(RasterFormat.Jpeg));
        Assert.False(SkiaImageEncoder.Supports(RasterFormat.Webp));
        Assert.False(SkiaImageEncoder.Supports(RasterFormat.Tiff));
    }
}
