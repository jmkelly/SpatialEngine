using System.Text.Json;
using Spatial.Host.Api;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Http;

namespace Spatial.Host.Tests;

/// <summary>
/// The content version behind a tile cache key (ADR-0046): the style, layer
/// descriptors, imagery stack and encoding options all fold into the hash.
/// </summary>
public sealed class TileFingerprintTests
{
    [Fact]
    public void The_same_request_hashes_to_the_same_version()
    {
        var first = TileEndpoints.Fingerprint(Request());
        var second = TileEndpoints.Fingerprint(Request());

        Assert.Equal(first, second);
        Assert.Equal(64, first.Length);
    }

    [Fact]
    public void A_style_change_changes_the_version()
    {
        var baseline = TileEndpoints.Fingerprint(Request());

        Assert.NotEqual(baseline, TileEndpoints.Fingerprint(Request() with { Style = Style(9) }));
    }

    [Fact]
    public void A_layer_or_store_change_changes_the_version()
    {
        var baseline = TileEndpoints.Fingerprint(Request());

        Assert.NotEqual(baseline, TileEndpoints.Fingerprint(Request() with
        {
            Layers = [new RenderLayerDto("demo.other", "demo")],
        }));
        Assert.NotEqual(baseline, TileEndpoints.Fingerprint(Request() with
        {
            Layers = [new RenderLayerDto("demo.cities", "warehouse")],
        }));
        Assert.NotEqual(baseline, TileEndpoints.Fingerprint(Request() with
        {
            Layers = [new RenderLayerDto("demo.cities", "demo", "name='London'")],
        }));
    }

    [Fact]
    public void A_format_or_scale_change_changes_the_version()
    {
        var baseline = TileEndpoints.Fingerprint(Request());

        Assert.NotEqual(baseline, TileEndpoints.Fingerprint(Request() with { Format = RasterFormat.Jpeg }));
        Assert.NotEqual(baseline, TileEndpoints.Fingerprint(Request() with { Scale = 2 }));
        Assert.NotEqual(baseline, TileEndpoints.Fingerprint(Request() with { Quality = 50 }));
    }

    [Fact]
    public void An_imagery_change_changes_the_version()
    {
        var baseline = TileEndpoints.Fingerprint(Request());

        Assert.NotEqual(baseline, TileEndpoints.Fingerprint(Request() with { Imagery = [new RenderImageryDto("basemap")] }));
        Assert.NotEqual(baseline, TileEndpoints.Fingerprint(Request() with
        {
            Imagery = [new RenderImageryDto("basemap", RasterBlend.Multiply, 0.5)],
        }));
    }

    private static TileRenderRequest Request() => new(
        Style(4),
        [new RenderLayerDto("demo.cities", "demo")]);

    private static JsonElement Style(int radius) =>
        JsonDocument.Parse($$"""
            { "version": 8, "layers": [
                { "id": "cities", "type": "circle", "source-layer": "demo.cities",
                  "paint": { "circle-radius": {{radius}} } } ] }
            """).RootElement.Clone();
}
