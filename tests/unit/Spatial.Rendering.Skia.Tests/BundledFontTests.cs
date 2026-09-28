using SkiaSharp;
using Spatial.Rendering.Skia.Drawing;

namespace Spatial.Rendering.Skia.Tests;

/// <summary>
/// Shaping over an embedded face: the pinned bytes, the resolved family, and
/// advances that grow with the size (ADR-0049, ADR-0080). The per-face digest
/// pins themselves are proven by <see cref="FontFaceRegistryTests"/>.
/// </summary>
public sealed class BundledFontTests
{
    [Fact]
    public void TheDefaultFaceIsNotoSansRegular()
    {
        Assert.Equal("Noto Sans", FontFaceRegistry.Default.DefaultFace.Family);
    }

    [Fact]
    public void CreateFontUsesTheBundledFamily()
    {
        using var font = BundledFont.CreateFont(FontFaceRegistry.Default.DefaultFace, 16);

        Assert.Equal("Noto Sans", font.Typeface.FamilyName);
    }

    [Fact]
    public void CreateFontSizesTheFace()
    {
        using var font = BundledFont.CreateFont(FontFaceRegistry.Default.DefaultFace, 22.5);

        Assert.Equal(22.5f, font.Size, 3);
    }

    [Fact]
    public void ShaperAdvancesGrowWithFontSize()
    {
        using var bundled = new BundledFont(FontFaceRegistry.Default.DefaultFace);
        using var small = BundledFont.CreateFont(FontFaceRegistry.Default.DefaultFace, 16);
        using var large = BundledFont.CreateFont(FontFaceRegistry.Default.DefaultFace, 32);

        var smallWidth = bundled.Shaper.Shape("London", small).Width;
        var largeWidth = bundled.Shaper.Shape("London", large).Width;

        Assert.True(smallWidth > 0);
        Assert.True(largeWidth > smallWidth);
    }

    [Fact]
    public void EachBundledFaceShapesItsOwnGlyphs()
    {
        using var regular = new BundledFont(FontFaceRegistry.Default.DefaultFace);
        using var bold = new BundledFont(FontFaceRegistry.Default.Resolve(["Noto Sans Bold"]));
        using var regularFont = BundledFont.CreateFont(FontFaceRegistry.Default.DefaultFace, 32);
        using var boldFont = BundledFont.CreateFont(FontFaceRegistry.Default.Resolve(["Noto Sans Bold"]), 32);

        var regularWidth = regular.Shaper.Shape("River", regularFont).Width;
        var boldWidth = bold.Shaper.Shape("River", boldFont).Width;

        Assert.True(boldWidth > regularWidth, $"Bold {boldWidth} should exceed regular {regularWidth}.");
    }

    [Fact]
    public void AShapedRunCarriesGlyphs()
    {
        using var bundled = new BundledFont(FontFaceRegistry.Default.DefaultFace);
        using var font = BundledFont.CreateFont(FontFaceRegistry.Default.DefaultFace, 16);

        var shaped = bundled.Shaper.Shape("Ålesund", font);

        Assert.True(shaped.Codepoints.Length > 0);
        Assert.True(shaped.Width > 0);
    }

    [Fact]
    public void ADisposedShaperIsReleasedOnce()
    {
        var bundled = new BundledFont(FontFaceRegistry.Default.DefaultFace);

        bundled.Dispose();
        bundled.Dispose();

        Assert.Equal(FontFaceRegistry.Default.DefaultFace, bundled.Face);
    }
}
