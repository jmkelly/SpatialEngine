using Spatial.Contracts;
using Spatial.Rendering.Skia.Drawing;

namespace Spatial.Rendering.Skia.Tests;

public sealed class SpriteRegistryTests
{
    private const string MarkerSvg =
        """<svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" viewBox="0 0 16 16"><circle cx="8" cy="8" r="8" fill="#00ff00"/></svg>""";

    /// <summary>
    /// The bundled marker set: a style may name any of these, and every one of
    /// them is parsed to a drawable picture (ADR-0080).
    /// </summary>
    [Fact]
    public void Default_CarriesTheMarkerSet()
    {
        Assert.Equal(
            ["default-marker", "marker-circle", "marker-diamond", "marker-ring", "marker-square", "marker-triangle"],
            SpriteRegistry.Default.Names.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("default-marker")]
    [InlineData("marker-circle")]
    [InlineData("marker-diamond")]
    [InlineData("marker-ring")]
    [InlineData("marker-square")]
    [InlineData("marker-triangle")]
    public void EveryBundledSpriteParsesToADrawablePicture(string name)
    {
        var picture = SpriteRegistry.Default.Get(name);

        Assert.False(picture.CullRect.IsEmpty);
        Assert.Equal(24f, picture.CullRect.Width);
        Assert.Equal(24f, picture.CullRect.Height);
    }

    [Fact]
    public void TheBundledIconNameIsStillTheDefaultMarker()
    {
        Assert.Contains(SpriteRegistry.DefaultName, SpriteRegistry.Default.Names);
        Assert.NotNull(SpriteRegistry.Default.Get(SpriteRegistry.DefaultName));
    }

    [Fact]
    public void Get_UnknownIconThrowsATypedErrorNamingWhatIsBundled()
    {
        var exception = Assert.Throws<SpatialException>(() => SpriteRegistry.Default.Get("absent"));

        Assert.Equal(SpatialException.InvalidArguments, exception.Code);
        Assert.Contains("marker-circle", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FromSources_ParsesEachIcon()
    {
        using var registry = SpriteRegistry.FromSources([new("marker", MarkerSvg)]);

        Assert.Equal(["marker"], registry.Names);
        Assert.Equal(16, registry.Get("marker").CullRect.Width);
    }

    [Fact]
    public void ADisposedRegistryRefusesToResolve()
    {
        var registry = SpriteRegistry.FromSources([new("marker", MarkerSvg)]);
        registry.Dispose();
        registry.Dispose();

        Assert.Empty(registry.Names);
        Assert.Throws<SpatialException>(() => registry.Get("marker"));
    }
}
