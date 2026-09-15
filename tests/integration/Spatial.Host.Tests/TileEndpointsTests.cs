using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Spatial.Contracts;
using Spatial.Contracts.Http;

namespace Spatial.Host.Tests;

/// <summary>
/// The tile routes (ADR-0046): cache-aware single tiles, ordered batches,
/// scheme discovery, cache invalidation and the failure mapping
/// for unknown schemes, out-of-range tiles and unconfigured formats.
/// </summary>
public sealed class TileEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string StyleDocument =
        """
        { "version": 8, "layers": [
            { "id": "bg", "type": "background", "paint": { "background-color": "#101820" } },
            { "id": "cities", "type": "circle", "source-layer": "demo.cities",
              "paint": { "circle-color": "#ffd166", "circle-radius": 6 } } ] }
        """;

    private readonly WebApplicationFactory<Program> _factory;

    public TileEndpointsTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Capabilities_describe_the_registered_scheme_and_batch_cap()
    {
        using var client = await FreshClientAsync();

        var response = await client.GetAsync("/api/render/tiles/capabilities");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<TileCapabilitiesResponse>();
        Assert.Equal("webmercator", body!.DefaultScheme);
        Assert.Equal(64, body.MaxTilesPerBatch);
        var scheme = Assert.Single(body.Schemes);
        Assert.Equal("EPSG:3857", scheme.Crs);
        Assert.Equal(256, scheme.TileSize);
        Assert.Equal(24, scheme.Levels.Count);
        Assert.Equal(156543.03392804097, scheme.Levels[0].Resolution, 6);
    }

    [Fact]
    public async Task A_tile_is_rendered_then_served_from_the_cache()
    {
        using var client = await FreshClientAsync();

        var first = await client.PostAsJsonAsync("/api/render/tiles/0/0/0.png", TileRequest());
        var second = await client.PostAsJsonAsync("/api/render/tiles/0/0/0.png", TileRequest());

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("false", first.Headers.GetValues("X-Tile-Cached").Single());
        Assert.Equal("image/png", first.Content.Headers.ContentType?.MediaType);
        Assert.Equal("256", first.Headers.GetValues("X-Raster-Width").Single());
        var firstBytes = await first.Content.ReadAsByteArrayAsync();
        Assert.Equal(0x89, firstBytes[0]);

        Assert.Equal("true", second.Headers.GetValues("X-Tile-Cached").Single());
        Assert.Equal(firstBytes, await second.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task The_path_format_is_authoritative()
    {
        using var client = await FreshClientAsync();

        var response = await client.PostAsJsonAsync("/api/render/tiles/2/1/1.jpeg", TileRequest(format: RasterFormat.Png));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task An_unconfigured_format_is_rejected()
    {
        using var client = await FreshClientAsync();

        var response = await client.PostAsJsonAsync("/api/render/tiles/0/0/0.tiff", TileRequest());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid.arguments", await CodeAsync(response));
    }

    [Fact]
    public async Task An_unknown_scheme_is_rejected()
    {
        using var client = await FreshClientAsync();

        var response = await client.PostAsJsonAsync("/api/render/tiles/0/0/0.png", TileRequest() with { Scheme = "mollweide" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid.arguments", await CodeAsync(response));
    }

    [Fact]
    public async Task An_out_of_range_tile_is_rejected()
    {
        using var client = await FreshClientAsync();

        var response = await client.PostAsJsonAsync("/api/render/tiles/0/1/0.png", TileRequest());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid.arguments", await CodeAsync(response));
    }

    [Fact]
    public async Task An_unknown_dataset_maps_to_404()
    {
        using var client = await FreshClientAsync();

        var response = await client.PostAsJsonAsync(
            "/api/render/tiles/0/0/0.png",
            TileRequest(dataset: "demo.absent"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("not.found", await CodeAsync(response));
    }

    [Fact]
    public async Task A_batch_returns_ordered_results_with_cache_dispositions()
    {
        using var client = await FreshClientAsync();
        var batch = new TileBatchRequest(TileRequest(), [new TileDto(2, 0, 0), new TileDto(2, 1, 0), new TileDto(2, 0, 1)]);

        var response = await client.PostAsJsonAsync("/api/render/tiles/batch", batch);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<TileBatchResponse>();
        Assert.Equal(3, body!.Tiles.Count);
        Assert.Equal([(2, 0, 0), (2, 1, 0), (2, 0, 1)], body.Tiles.Select(tile => (tile.Z, tile.X, tile.Y)));
        Assert.All(body.Tiles, tile =>
        {
            Assert.False(tile.Cached);
            Assert.Equal("image/png", tile.ContentType);
            Assert.Equal(0x89, Convert.FromBase64String(tile.Content)[0]);
        });
    }

    [Fact]
    public async Task Clearing_the_cache_makes_the_next_tile_a_miss()
    {
        using var client = await FreshClientAsync();
        _ = await client.PostAsJsonAsync("/api/render/tiles/0/0/0.png", TileRequest());

        var cleared = await client.DeleteAsync("/api/render/cache");
        var after = await client.PostAsJsonAsync("/api/render/tiles/0/0/0.png", TileRequest());

        Assert.Equal(HttpStatusCode.NoContent, cleared.StatusCode);
        Assert.Equal("false", after.Headers.GetValues("X-Tile-Cached").Single());
    }

    [Fact]
    public async Task A_tile_renders_symbol_labels()
    {
        using var client = await FreshClientAsync();
        var style = JsonDocument.Parse(
            """
            { "version": 8, "layers": [
                { "id": "labels", "type": "symbol", "source-layer": "demo.cities",
                  "layout": { "text-field": "{name}", "text-size": 10, "text-anchor": "left",
                              "icon-image": "default-marker", "icon-size": 0.5, "text-offset": [0.4, 0] },
                  "paint": { "text-color": "#ffffff", "text-halo-color": "#0b1220", "text-halo-width": 1 } } ] }
            """).RootElement.Clone();

        var response = await client.PostAsJsonAsync("/api/render/tiles/0/0/0.png", TileRequest() with { Style = style });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(0x89, bytes[0]);
    }

    private async Task<HttpClient> FreshClientAsync()
    {
        var client = _factory.CreateClient();
        _ = await client.DeleteAsync("/api/render/cache");
        return client;
    }

    private static TileRenderRequest TileRequest(string dataset = "demo.cities", RasterFormat format = RasterFormat.Png) => new(
        JsonDocument.Parse(StyleDocument.Replace("demo.cities", dataset, StringComparison.Ordinal)).RootElement.Clone(),
        [new RenderLayerDto(dataset, "demo")],
        Format: format);

    private static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<TileErrorBody>();
        return body?.Code;
    }

    private sealed record TileErrorBody(string Code, string Message);
}
