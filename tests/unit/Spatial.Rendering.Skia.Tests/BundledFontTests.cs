using System.Security.Cryptography;
using Spatial.Rendering.Skia.Drawing;

namespace Spatial.Rendering.Skia.Tests;

public sealed class BundledFontTests
{
    [Fact]
    public void EmbeddedFontMatchesThePinnedHash()
    {
        var bytes = ManifestResources.Read("Fonts.NotoSans-Regular.ttf");
        var hash = Convert.ToHexString(SHA256.HashData(bytes));

        Assert.Equal(BundledFont.ExpectedSha256, hash);
    }

    [Fact]
    public void CreateFontUsesTheBundledFamily()
    {
        using var font = BundledFont.CreateFont(16);

        Assert.Equal(BundledFont.FamilyName, font.Typeface.FamilyName);
    }

    [Fact]
    public void ShaperAdvancesGrowWithFontSize()
    {
        using var bundled = new BundledFont();
        using var small = BundledFont.CreateFont(16);
        using var large = BundledFont.CreateFont(32);

        var smallWidth = bundled.Shaper.Shape("London", small).Width;
        var largeWidth = bundled.Shaper.Shape("London", large).Width;

        Assert.True(smallWidth > 0);
        Assert.True(largeWidth > smallWidth);
    }
}
