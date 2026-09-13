using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Spatial.Host.Tests;

/// <summary>
/// The neutral map tile route (ADR-0053 §3): a map whose Tiles service is
/// enabled renders its persisted-style feature layers through the shared tile
/// pipeline; a map without the service is not-found.
/// </summary>
public sealed class MapTileTests : IDisposable
{
    private const string Token = "test-admin-token";

    private const string Circle =
        """[{"type":"circle","layout":{"visibility":"visible"},"paint":{"circle-color":"#ff0000","circle-radius":8}}]""";

    private readonly string _directory = Directory.CreateTempSubdirectory("spatial-map-tiles-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private WebApplicationFactory<Program> Factory() => new MapTileFactory(Path.Combine(_directory, "maps.json"));

    private static async Task PutMapAsync(HttpClient client, string name, string[] services, string? style = Circle)
    {
        var body = JsonSerializer.Serialize(new
        {
            name,
            store = "demo",
            services,
            layers = new[] { new { dataset = "demo.cities", layerId = 0, name = "cities", style } },
        });
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/maps/{name}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_tiled_map_renders_a_png_tile()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        await PutMapAsync(client, "tiled", ["tiles"]);

        var response = await client.GetAsync("/api/maps/tiled/tiles/0/0/0.png");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("256", response.Headers.GetValues("X-Raster-Width").Single());
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(0x89, bytes[0]);
    }

    [Fact]
    public async Task The_path_format_is_authoritative()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        await PutMapAsync(client, "tiled", ["tiles"]);

        var response = await client.GetAsync("/api/maps/tiled/tiles/2/1/1.jpeg");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Query_options_reach_the_renderer()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        await PutMapAsync(client, "tiled", ["tiles"]);

        var response = await client.GetAsync("/api/maps/tiled/tiles/0/0/0.png?quality=50&background=%23000000&transparent=true&scale=2");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("512", response.Headers.GetValues("X-Raster-Width").Single());
    }

    [Fact]
    public async Task The_cache_key_follows_the_composed_style()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        await PutMapAsync(client, "tiled", ["tiles"], Circle);
        var first = await client.GetAsync("/api/maps/tiled/tiles/0/0/0.png");

        await PutMapAsync(
            client,
            "tiled",
            ["tiles"],
            """[{"type":"circle","layout":{"visibility":"visible"},"paint":{"circle-color":"#0000ff","circle-radius":40,"circle-opacity":1.0}}]""");
        var second = await client.GetAsync("/api/maps/tiled/tiles/0/0/0.png");

        Assert.NotEqual(
            await first.Content.ReadAsByteArrayAsync(),
            await second.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task A_map_without_the_tiles_service_is_not_found()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        await PutMapAsync(client, "draft", []);

        var response = await client.GetAsync("/api/maps/draft/tiles/0/0/0.png");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("not.found", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task An_unknown_map_is_not_found()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/maps/absent/tiles/0/0/0.png");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("not.found", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task An_out_of_range_tile_is_rejected()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        await PutMapAsync(client, "tiled", ["tiles"]);

        var response = await client.GetAsync("/api/maps/tiled/tiles/0/1/0.png");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("invalid.arguments", body.GetProperty("code").GetString());
    }

    /// <summary>A host with an admin token and a per-test map file.</summary>
    private sealed class MapTileFactory(string mapsPath) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Spatial:Admin:Token", Token);
            builder.UseSetting("Spatial:Maps:Path", mapsPath);
        }
    }
}
