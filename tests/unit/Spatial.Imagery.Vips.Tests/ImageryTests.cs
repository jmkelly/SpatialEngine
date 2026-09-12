using NetVips;
using Spatial.Imagery.Vips.Imagery;
using Spatial.PluginSdk;

namespace Spatial.Imagery.Vips.Tests;

public sealed class VipsColorTests
{
    [Theory]
    [InlineData("#fff", 255, 255, 255, 255)]
    [InlineData("#ff8000", 255, 128, 0, 255)]
    [InlineData("#ff800080", 255, 128, 0, 128)]
    [InlineData("rgb(1, 2, 3)", 1, 2, 3, 255)]
    [InlineData("rgba(1,2,3,0.5)", 1, 2, 3, 128)]
    [InlineData("white", 255, 255, 255, 255)]
    public void TryParse_ReadsTheCssSubset(string raw, byte red, byte green, byte blue, byte alpha)
    {
        Assert.True(VipsColorParser.TryParse(raw, out var color));
        Assert.Equal(new VipsColor(red, green, blue, alpha), color);
    }

    [Theory]
    [InlineData("#12")]
    [InlineData("#gggggg")]
    [InlineData("rgb(1,2)")]
    [InlineData("rgb(a,b,c)")]
    [InlineData("rgba(1,2,3,0.5,0.5)")]
    [InlineData("mauve")]
    public void TryParse_RejectsUnsupportedSyntax(string raw)
    {
        Assert.False(VipsColorParser.TryParse(raw, out _));
    }

    [Fact]
    public void Parse_ThrowsOnUnsupportedColour()
    {
        Assert.Equal(SpatialException.InvalidArguments, Assert.Throws<SpatialException>(() => VipsColorParser.Parse("mauve")).Code);
        Assert.Equal(SpatialException.InvalidArguments, Assert.Throws<SpatialException>(() => VipsColorParser.Parse(string.Empty)).Code);
    }
}

public sealed class ImageryLoaderTests
{
    [Fact]
    public void Resolve_ReturnsAConfiguredExistingFile()
    {
        using var fixture = new ImageryFixture();
        Assert.Equal(fixture.Path, ImageryLoader.Resolve(fixture.Sources, "basemap"));
    }

    [Fact]
    public void Resolve_RejectsUnknownMissingAndEmptySources()
    {
        using var fixture = new ImageryFixture();
        var sources = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["gone"] = Path.Combine(Path.GetTempPath(), "does-not-exist.png"),
        };

        Assert.Equal(SpatialException.InvalidArguments, Assert.Throws<SpatialException>(() => ImageryLoader.Resolve(sources, "other")).Code);
        Assert.Equal(SpatialException.NotFound, Assert.Throws<SpatialException>(() => ImageryLoader.Resolve(sources, "gone")).Code);
        Assert.Equal(SpatialException.InvalidArguments, Assert.Throws<SpatialException>(() => ImageryLoader.Resolve(fixture.Sources, null)).Code);
    }

    [Fact]
    public void LoadExact_ResizesToTheRequestedSize()
    {
        using var fixture = new ImageryFixture();
        using var image = ImageryLoader.LoadExact(fixture.Path, 7, 3);
        Assert.Equal(7, image.Width);
        Assert.Equal(3, image.Height);
        Assert.Equal(4, image.Bands);
        Assert.Equal(Enums.Interpretation.Srgb, image.Interpretation);
    }

    [Fact]
    public void Solid_CreatesARgbImage()
    {
        using var image = ImageryLoader.Solid(5, 4, new VipsColor(10, 20, 30, 255));
        Assert.Equal(5, image.Width);
        Assert.Equal(4, image.Height);
        Assert.Equal(4, image.Bands);
    }

    [Fact]
    public void FromBuffer_UnpremultipliesAndTagsSrgb()
    {
        var buffer = Buffer(new byte[] { 128, 0, 0, 128 }, 1, 1, RasterPixelFormat.Rgba8888, premultiplied: true);
        using var image = ImageryLoader.FromBuffer(buffer);
        Assert.Equal(4, image.Bands);
        Assert.Equal(Enums.Interpretation.Srgb, image.Interpretation);
        using var straight = image.Unpremultiply();
        Assert.Equal(4, straight.Bands);
    }

    [Fact]
    public void FromBuffer_AddsAlphaToRgbBuffers()
    {
        var buffer = Buffer(new byte[] { 1, 2, 3 }, 1, 1, RasterPixelFormat.Rgb888, premultiplied: false);
        using var image = ImageryLoader.FromBuffer(buffer);
        Assert.Equal(4, image.Bands);
    }

    [Fact]
    public void FromBuffer_RepacksPaddedRows()
    {
        var padded = Buffer(
            new byte[] { 1, 2, 3, 4, 9, 9, 9, 9, 5, 6, 7, 8, 9, 9, 9, 9 },
            2,
            1,
            RasterPixelFormat.Rgba8888,
            premultiplied: false,
            stride: 12);
        using var image = ImageryLoader.FromBuffer(padded);
        Assert.Equal(2, image.Width);
        Assert.Equal(1, image.Height);
    }

    internal static RasterBuffer Buffer(byte[] pixels, int width, int height, RasterPixelFormat format, bool premultiplied, int? stride = null) =>
        new(pixels, width, height, stride ?? width * (format == RasterPixelFormat.Rgba8888 ? 4 : 3), format, premultiplied);
}

public sealed class RasterBlendingTests
{
    [Theory]
    [InlineData(RasterBlend.Over, Enums.BlendMode.Over)]
    [InlineData(RasterBlend.Multiply, Enums.BlendMode.Multiply)]
    [InlineData(RasterBlend.Screen, Enums.BlendMode.Screen)]
    [InlineData(RasterBlend.Darken, Enums.BlendMode.Darken)]
    [InlineData(RasterBlend.Lighten, Enums.BlendMode.Lighten)]
    public void ToVips_MapsEveryBlend(RasterBlend blend, Enums.BlendMode expected)
    {
        Assert.Equal(expected, RasterBlending.ToVips(blend));
    }

    [Fact]
    public void ToVips_RejectsUnknownBlends()
    {
        Assert.Equal(SpatialException.InvalidArguments, Assert.Throws<SpatialException>(() => RasterBlending.ToVips((RasterBlend)99)).Code);
    }

    [Fact]
    public void ApplyOpacity_FullOpacityReturnsACopy()
    {
        using var source = ImageryLoader.Solid(2, 2, new VipsColor(255, 0, 0, 255));
        using var result = RasterBlending.ApplyOpacity(source, 1);
        Assert.Equal(4, result.Bands);
    }

    [Fact]
    public void ApplyOpacity_ScalesTheAlphaBand()
    {
        using var source = ImageryLoader.Solid(2, 2, new VipsColor(255, 0, 0, 255));
        using var result = RasterBlending.ApplyOpacity(source, 0.5);
        using var alpha = result.ExtractBand(3);
        Assert.Equal(128.0, alpha.Avg(), 0.5);
    }

    [Theory]
    [InlineData(-0.5)]
    [InlineData(1.5)]
    public void ApplyOpacity_RejectsOutOfRangeValues(double opacity)
    {
        using var source = ImageryLoader.Solid(2, 2, new VipsColor(255, 0, 0, 255));
        Assert.Equal(SpatialException.InvalidArguments, Assert.Throws<SpatialException>(() => RasterBlending.ApplyOpacity(source, opacity)).Code);
    }
}

public sealed class VipsEncoderTests
{
    [Theory]
    [InlineData(RasterFormat.Png, "image/png")]
    [InlineData(RasterFormat.Jpeg, "image/jpeg")]
    [InlineData(RasterFormat.Webp, "image/webp")]
    [InlineData(RasterFormat.Tiff, "image/tiff")]
    public void Encode_ProducesTheRequestedContainer(RasterFormat format, string mediaType)
    {
        using var image = ImageryLoader.Solid(4, 4, new VipsColor(10, 20, 30, 255));
        var encoded = VipsEncoder.Encode(image, format, 90);
        Assert.Equal(mediaType, encoded.MediaType);
        Assert.Equal(format, encoded.Format);
        Assert.NotEmpty(encoded.Content);
    }

    [Fact]
    public void Encode_RejectsUnknownFormats()
    {
        using var image = ImageryLoader.Solid(4, 4, new VipsColor(10, 20, 30, 255));
        Assert.Equal(SpatialException.InvalidArguments, Assert.Throws<SpatialException>(() => VipsEncoder.Encode(image, (RasterFormat)99, 90)).Code);
    }
}

/// <summary>A tiny generated imagery file plus its configured source map.</summary>
internal sealed class ImageryFixture : IDisposable
{
    public ImageryFixture()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"spatial-imagery-{Guid.NewGuid():N}.png");
        using var image = ImageryLoader.Solid(8, 8, new VipsColor(20, 40, 60, 255));
        image.WriteToFile(Path);
        Sources = new Dictionary<string, string>(StringComparer.Ordinal) { ["basemap"] = Path };
    }

    public string Path { get; }

    public IReadOnlyDictionary<string, string> Sources { get; }

    public void Dispose()
    {
        if (File.Exists(Path))
        {
            File.Delete(Path);
        }
    }
}
