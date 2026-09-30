using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace Spatial.Host.Tests;

/// <summary>
/// The Esri <c>MapServer/vectorTile/{z}/{y}/{x}</c> route (ADR-0070): a live
/// MVT view over a MapServer's feature layers, served from the same
/// scheme/version cache key as the neutral route, with the Esri error shape
/// when the service is unknown.
/// </summary>
public sealed class EsriVectorTileRouteTests : IDisposable
{
    private const string Root = "/arcgis/rest/services";
    private static readonly string[] Services = ["map"];

    private readonly string _directory = Directory.CreateTempSubdirectory("spatial-esri-vector-tile-").FullName;
    private readonly SpatialHostFactory _factory;

    public EsriVectorTileRouteTests() => _factory = new EsriVectorTileFactory(Path.Combine(_directory, "maps.json"));

    public void Dispose()
    {
        _factory.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task The_esri_route_serves_a_vector_tile_then_its_cached_bytes()
    {
        using var client = _factory.CreateClient();
        await PublishAsync(client, "cities");

        using var first = await client.GetAsync($"{Root}/cities/MapServer/vectorTile/0/0/0");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("application/vnd.mapbox-vector-tile", first.Content.Headers.ContentType?.MediaType);
        Assert.Equal("false", first.Headers.GetValues("X-Tile-Cached").Single());
        var bytes = await first.Content.ReadAsByteArrayAsync();
        Assert.NotEmpty(bytes);

        using var second = await client.GetAsync($"{Root}/cities/MapServer/vectorTile/0/0/0");
        Assert.Equal("true", second.Headers.GetValues("X-Tile-Cached").Single());
        Assert.Equal(bytes, await second.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task An_unknown_service_is_an_esri_not_found_error()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync($"{Root}/absent/MapServer/vectorTile/0/0/0");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(404, document.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    private static async Task PublishAsync(HttpClient client, string name)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/maps/{name}")
        {
            Content = JsonContent.Create(new
            {
                name,
                store = "demo",
                layers = new[] { new { dataset = "demo.world_cities", layerId = 0, name = "cities" } },
                services = Services,
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "test-admin");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private sealed class EsriVectorTileFactory(string mapsPath) : SpatialHostFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Spatial:Maps:Path", mapsPath);
            builder.UseSetting("Spatial:Admin:Token", "test-admin");
        }
    }
}
