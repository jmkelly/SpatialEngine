using Spatial.Contracts;
using Spatial.Rendering.Skia.Drawing;

namespace Spatial.Rendering.Skia.Tests;

public sealed class SpriteRegistryTests
{
    private const string MarkerSvg =
        """<svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" viewBox="0 0 16 16"><circle cx="8" cy="8" r="8" fill="#00ff00"/></svg>""";

    [Fact]
    public void Default_ContainsTheBundledIcon()
    {
        Assert.Contains(SpriteRegistry.DefaultName, SpriteRegistry.Default.Names);
        Assert.NotNull(SpriteRegistry.Default.Get(SpriteRegistry.DefaultName));
    }

    [Fact]
    public void Get_UnknownIconThrowsATypedError()
    {
        var exception = Assert.Throws<SpatialException>(() => SpriteRegistry.Default.Get("absent"));
        Assert.Equal(SpatialException.InvalidArguments, exception.Code);
    }

    [Fact]
    public void FromSources_ParsesEachIcon()
    {
        using var registry = SpriteRegistry.FromSources([new("marker", MarkerSvg)]);

        Assert.Equal(["marker"], registry.Names);
        Assert.Equal(16, registry.Get("marker").CullRect.Width);
    }
}
