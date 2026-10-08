using SkiaSharp;
using Spatial.Rendering.Skia.Drawing;

namespace Spatial.Rendering.Skia.Tests;

/// <summary>
/// The style names of the bundled faces: every CSS weight maps to its
/// documented name, and the slant reads as the face name a style would write.
/// </summary>
public sealed class FontFaceTests
{
    [Theory]
    [InlineData(100, "Thin")]
    [InlineData(200, "Extra Light")]
    [InlineData(300, "Light")]
    [InlineData(400, "Regular")]
    [InlineData(500, "Medium")]
    [InlineData(600, "SemiBold")]
    [InlineData(700, "Bold")]
    [InlineData(800, "Extra Bold")]
    [InlineData(900, "Black")]
    [InlineData(350, "350")]
    public void WeightNameFor_MapsEveryCssWeight(int weight, string expected) =>
        Assert.Equal(expected, FontFace.WeightNameFor(weight));

    [Theory]
    [InlineData(400, 0, "Noto Sans Regular")]
    [InlineData(700, 0, "Noto Sans Bold")]
    [InlineData(100, 0, "Noto Sans Thin")]
    [InlineData(450, 0, "Noto Sans 450")]
    [InlineData(400, 1, "Noto Sans Italic")]
    [InlineData(700, 1, "Noto Sans Bold Italic")]
    [InlineData(900, 1, "Noto Sans Black Italic")]
    public void Name_NamesTheFaceAsAStyleWould(int weight, int style, string expected) =>
        Assert.Equal(expected, new FontFace("Noto Sans", weight, (FontStyle)style, "resource", "digest").Name);

    [Theory]
    [InlineData(0, SKFontStyleSlant.Upright)]
    [InlineData(1, SKFontStyleSlant.Italic)]
    public void Slant_MatchesTheFaceStyle(int style, SKFontStyleSlant expected) =>
        Assert.Equal(expected, new FontFace("Noto Sans", 400, (FontStyle)style, "resource", "digest").Slant);
}
