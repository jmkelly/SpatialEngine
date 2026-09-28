using System.Text.Json;
using Spatial.Contracts;
using Spatial.Contracts.Http;
using Spatial.Host.Api;

namespace Spatial.Host.Tests;

/// <summary>
/// The content version behind a tile cache key (ADR-0046, ADR-0083): the style,
/// layer descriptors, imagery stack, encoding options and the folded content
/// version of the request's datasets all fold into the hash.
/// </summary>
public sealed class TileFingerprintTests
{
    /// <summary>The folded content version of the request's datasets (ADR-0083).</summary>
    private const string DataVersion = "content";

    [Fact]
    public void The_same_request_hashes_to_the_same_version()
    {
        var first = TileEndpoints.Fingerprint(Request(), DataVersion);
        var second = TileEndpoints.Fingerprint(Request(), DataVersion);

        Assert.Equal(first, second);
        Assert.Equal(64, first.Length);
    }

    [Fact]
    public void A_style_change_changes_the_version()
    {
        var baseline = TileEndpoints.Fingerprint(Request(), DataVersion);

        Assert.NotEqual(baseline, TileEndpoints.Fingerprint(Request() with { Style = Style(9) }, DataVersion));
    }

    [Fact]
    public void A_layer_or_store_change_changes_the_version()
    {
        var baseline = TileEndpoints.Fingerprint(Request(), DataVersion);

        Assert.NotEqual(baseline, TileEndpoints.Fingerprint(Request() with
        {
            Layers = [new RenderLayerDto("demo.other", "demo")],
        }, DataVersion));
        Assert.NotEqual(baseline, TileEndpoints.Fingerprint(Request() with
        {
            Layers = [new RenderLayerDto("demo.cities", "warehouse")],
        }, DataVersion));
        Assert.NotEqual(baseline, TileEndpoints.Fingerprint(Request() with
        {
            Layers = [new RenderLayerDto("demo.cities", "demo", "name='London'")],
        }, DataVersion));
    }

    [Fact]
    public void A_format_or_scale_change_changes_the_version()
    {
        var baseline = TileEndpoints.Fingerprint(Request(), DataVersion);

        Assert.NotEqual(baseline, TileEndpoints.Fingerprint(Request() with { Format = RasterFormat.Jpeg }, DataVersion));
        Assert.NotEqual(baseline, TileEndpoints.Fingerprint(Request() with { Scale = 2 }, DataVersion));
        Assert.NotEqual(baseline, TileEndpoints.Fingerprint(Request() with { Quality = 50 }, DataVersion));
    }

    [Fact]
    public void An_imagery_change_changes_the_version()
    {
        var baseline = TileEndpoints.Fingerprint(Request(), DataVersion);

        Assert.NotEqual(baseline, TileEndpoints.Fingerprint(Request() with { Imagery = [new RenderImageryDto("basemap")] }, DataVersion));
        Assert.NotEqual(baseline, TileEndpoints.Fingerprint(Request() with
        {
            Imagery = [new RenderImageryDto("basemap", RasterBlend.Multiply, 0.5)],
        }, DataVersion));
    }

    [Fact]
    public void A_data_version_change_changes_the_version()
    {
        var baseline = TileEndpoints.Fingerprint(Request(), DataVersion);

        Assert.NotEqual(baseline, TileEndpoints.Fingerprint(Request(), "moved"));
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
