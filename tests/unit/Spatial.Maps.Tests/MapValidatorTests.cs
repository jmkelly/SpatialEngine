using Spatial.Maps;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Providers;

namespace Spatial.Maps.Tests;

/// <summary>
/// Pins <see cref="MapValidator"/>'s layer arms (ADR-0053 §2): image layers
/// follow the raster-dataset grammar, a repeated dataset and an empty layer
/// name fail as <c>invalid.arguments</c>.
/// </summary>
public sealed class MapValidatorTests
{
    [Fact]
    public void Normalize_accepts_an_image_layer_with_a_raster_dataset()
    {
        var map = new Map(
            "ortho",
            "raster",
            [new MapLayer("ortho_2024.tif", 0, Kind: MapLayerKind.Image)],
            [MapServiceKind.ImageServer]);

        var normalised = MapValidator.Normalize(map, nextLayerId: 0);

        Assert.Equal("ortho_2024.tif", Assert.Single(normalised.Layers).Dataset);
    }

    [Fact]
    public void Normalize_rejects_an_image_layer_with_a_non_raster_dataset()
    {
        var map = new Map(
            "ortho",
            "raster",
            [new MapLayer("has space", 0, Kind: MapLayerKind.Image)],
            [MapServiceKind.ImageServer]);

        var failure = Assert.Throws<SpatialException>(() => MapValidator.Normalize(map, nextLayerId: 0));

        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
        Assert.Contains("raster dataset identifier", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Normalize_rejects_a_repeated_dataset()
    {
        var map = new Map(
            "dup",
            "memory",
            [new MapLayer("memory.parks", 0), new MapLayer("memory.parks", 1, Name: "again")],
            [MapServiceKind.FeatureServer]);

        var failure = Assert.Throws<SpatialException>(() => MapValidator.Normalize(map, nextLayerId: 0));

        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
        Assert.Contains("more than once", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Normalize_rejects_an_empty_layer_name()
    {
        var map = new Map(
            "named",
            "memory",
            [new MapLayer("memory.parks", 0, Name: string.Empty)],
            [MapServiceKind.FeatureServer]);

        var failure = Assert.Throws<SpatialException>(() => MapValidator.Normalize(map, nextLayerId: 0));

        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
        Assert.Contains("empty name", failure.Message, StringComparison.Ordinal);
    }
}
