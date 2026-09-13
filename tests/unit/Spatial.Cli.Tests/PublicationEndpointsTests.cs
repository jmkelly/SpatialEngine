using Spatial.Cli;
using Spatial.PluginSdk.Providers;

namespace Spatial.Cli.Tests;

/// <summary>GeoServices endpoint shaping per publication kind (ADR-0035/0048/0051).</summary>
public sealed class PublicationEndpointsTests
{
    [Theory]
    [InlineData(PublicationKind.Feature, "FeatureServer")]
    [InlineData(PublicationKind.Map, "MapServer")]
    [InlineData(PublicationKind.Image, "ImageServer")]
    public void For_maps_the_publication_kind_to_a_service_type(PublicationKind kind, string serviceType)
    {
        var publication = new Publication("World", kind, "memory", []);

        var endpoint = PublicationEndpoints.For("http://host:1/", "/arcgis/rest/services", publication);

        Assert.Equal($"http://host:1/arcgis/rest/services/World/{serviceType}", endpoint);
    }

    [Fact]
    public void Root_normalises_a_missing_leading_slash()
    {
        Assert.Equal("http://host:1/arcgis/rest/services", PublicationEndpoints.Root("http://host:1", "arcgis/rest/services"));
    }

    [Fact]
    public void Root_falls_back_to_the_default_for_an_empty_root()
    {
        Assert.Equal("http://host:1/arcgis/rest/services", PublicationEndpoints.Root("http://host:1", "  "));
    }
}
