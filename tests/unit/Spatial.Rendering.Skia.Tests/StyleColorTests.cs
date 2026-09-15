using Spatial.Rendering.Skia.Styling;

namespace Spatial.Rendering.Skia.Tests;

public sealed class StyleColorTests
{
    [Theory]
    [InlineData("#fff", 255, 255, 255, 255)]
    [InlineData("#FFF", 255, 255, 255, 255)]
    [InlineData("#0000", 0, 0, 0, 0)]
    [InlineData("#f008", 255, 0, 0, 136)]
    [InlineData("#ff8000", 255, 128, 0, 255)]
    [InlineData("#ff800080", 255, 128, 0, 128)]
    [InlineData("rgb(1, 2, 3)", 1, 2, 3, 255)]
    [InlineData("rgba(1,2,3,0.5)", 1, 2, 3, 128)]
    [InlineData("white", 255, 255, 255, 255)]
    [InlineData("LightGrey", 211, 211, 211, 255)]
    [InlineData("transparent", 0, 0, 0, 0)]
    public void TryParse_ReadsTheCssSubset(string raw, byte red, byte green, byte blue, byte alpha)
    {
        Assert.True(StyleColorParser.TryParse(raw, out var color));
        Assert.Equal(new StyleColor(red, green, blue, alpha), color);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("#")]
    [InlineData("#12")]
    [InlineData("#12345")]
    [InlineData("#gggggg")]
    [InlineData("rgb(1,2)")]
    [InlineData("rgb(a,b,c)")]
    [InlineData("rgba(1,2,3,0.5,0.5)")]
    [InlineData("rgba(1,2,3,notanumber)")]
    [InlineData("mauve")]
    public void TryParse_RejectsUnsupportedSyntax(string raw)
    {
        Assert.False(StyleColorParser.TryParse(raw, out _));
    }

    [Fact]
    public void ScaleAlpha_MultipliesAndClamps()
    {
        Assert.Equal(new StyleColor(10, 20, 30, 128), new StyleColor(10, 20, 30, 255).ScaleAlpha(0.5));
        Assert.Equal(new StyleColor(10, 20, 30, 255), new StyleColor(10, 20, 30, 200).ScaleAlpha(2));
        Assert.Equal(new StyleColor(10, 20, 30, 0), new StyleColor(10, 20, 30, 200).ScaleAlpha(0));
    }
}
