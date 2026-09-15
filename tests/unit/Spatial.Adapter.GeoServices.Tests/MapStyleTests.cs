using System.Text.Json;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Providers;

namespace Spatial.Adapter.GeoServices.Tests;

/// <summary>
/// The shared map style composer (ADR-0047/ADR-0048/ADR-0053): persisted
/// fragments gain a unique <c>id</c> and their dataset as
/// <c>source-layer</c>; a layer with no style falls back to the neutral
/// symbol so a MapServer always has something to draw.
/// </summary>
public sealed class MapStyleTests
{
    [Fact]
    public void Injects_id_and_source_layer_for_each_fragment()
    {
        var map = new Map("svc", "demo", [
            new MapLayer("demo.cities", 3, null, """[{"type":"circle","paint":{"circle-color":"#ff0000"}}]"""),
        ], [MapServiceKind.MapServer]);

        using var document = JsonDocument.Parse(MapStyle.Compose(map));
        var layer = document.RootElement.GetProperty("layers")[0];

        Assert.Equal("3-0", layer.GetProperty("id").GetString());
        Assert.Equal("demo.cities", layer.GetProperty("source-layer").GetString());
        Assert.Equal("#ff0000", layer.GetProperty("paint").GetProperty("circle-color").GetString());
    }

    [Fact]
    public void A_layer_without_style_keeps_a_default_symbol()
    {
        var map = new Map("svc", "demo", [new MapLayer("demo.cities", 0)], [MapServiceKind.MapServer]);

        using var document = JsonDocument.Parse(MapStyle.Compose(map));
        var layers = document.RootElement.GetProperty("layers").EnumerateArray().ToArray();

        Assert.Equal(3, layers.Length);
        Assert.All(layers, layer => Assert.Equal("demo.cities", layer.GetProperty("source-layer").GetString()));
    }

    [Fact]
    public void Only_the_selected_layers_are_composed()
    {
        var layers = new List<MapLayer>
        {
            new("demo.a", 0, null, """[{"type":"line","paint":{"line-color":"#000000"}}]"""),
            new("demo.b", 1, null, """[{"type":"line","paint":{"line-color":"#ffffff"}}]"""),
        };

        using var document = JsonDocument.Parse(MapStyle.Compose("svc", [layers[1]]));
        var composed = document.RootElement.GetProperty("layers").EnumerateArray().ToArray();

        Assert.Single(composed);
        Assert.Equal("demo.b", composed[0].GetProperty("source-layer").GetString());
    }
}
