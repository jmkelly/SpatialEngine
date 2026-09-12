using Spatial.Host.Api;

namespace Spatial.Host.Tests;

/// <summary>The raster host configuration (ADR-0044): advertised formats and the imagery source map.</summary>
public sealed class RenderingOptionsTests
{
    [Fact]
    public void Default_rendering_options_advertise_the_documented_limits()
    {
        var options = new RenderingOptions();

        Assert.Contains("png", options.Formats);
        Assert.Contains("webp", options.Formats);
        Assert.Equal(16_777_216, options.MaxPixels);
        Assert.Equal(32, options.MaxLayers);
    }

    [Fact]
    public void ToSourceMap_keeps_only_named_sources_with_paths()
    {
        var options = new ImageryOptions
        {
            Sources =
            [
                new ImageryOptions.ImagerySource { Name = "basemap", Path = "/data/base.tif" },
                new ImageryOptions.ImagerySource { Name = " ", Path = "/data/x.tif" },
                new ImageryOptions.ImagerySource { Name = "demo", Path = string.Empty },
            ],
        };

        var map = options.ToSourceMap();

        Assert.Equal("/data/base.tif", map["basemap"]);
        Assert.Single(map);
    }
}
