using Spatial.Cli;
using Spatial.PluginSdk.Providers;

namespace Spatial.Cli.Tests;

/// <summary>GeoServices endpoint shaping per map kind (ADR-0035/0048/0051).</summary>
public sealed class MapEndpointsTests
{
    [Theory]
    [InlineData(MapServiceKind.FeatureServer, "FeatureServer")]
    [InlineData(MapServiceKind.MapServer, "MapServer")]
    [InlineData(MapServiceKind.ImageServer, "ImageServer")]
    public void For_maps_the_publication_kind_to_a_service_type(MapServiceKind kind, string serviceType)
    {
        var map = new Map("World", "memory", [], [kind]);

        var endpoint = MapEndpoints.For("http://host:1/", "/arcgis/rest/services", map);

        Assert.Equal($"http://host:1/arcgis/rest/services/World/{serviceType}", endpoint);
    }

    [Fact]
    public void Root_normalises_a_missing_leading_slash()
    {
        Assert.Equal("http://host:1/arcgis/rest/services", MapEndpoints.Root("http://host:1", "arcgis/rest/services"));
    }

    [Fact]
    public void Root_falls_back_to_the_default_for_an_empty_root()
    {
        Assert.Equal("http://host:1/arcgis/rest/services", MapEndpoints.Root("http://host:1", "  "));
    }
}
