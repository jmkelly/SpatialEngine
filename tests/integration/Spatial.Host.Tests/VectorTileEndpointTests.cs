using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;

namespace Spatial.Host.Tests;

public sealed class VectorTileEndpointTests : IDisposable
{
    private readonly string _directory;
    private readonly SpatialHostFactory _factory;

    public VectorTileEndpointTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "spatial-vector-tile-" + Guid.NewGuid().ToString("N"));
        _factory = new VectorTileFactory(_directory);
    }

    [Fact]
    public async Task Neutral_mvt_route_returns_cached_bytes_and_maps_a_missing_name_to_not_found()
    {
        using var client = _factory.CreateClient();
        using var putRequest = new HttpRequestMessage(HttpMethod.Put, "/api/maps/vector")
        {
            Content = JsonContent.Create(new
            {
                name = "vector",
                store = "demo",
                layers = new[] { new { dataset = "demo.world_cities", layerId = 0, name = "cities" } },
                services = TileServices,
            }),
        };
        putRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "test-admin");
        using var put = await client.SendAsync(putRequest);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        using var first = await client.GetAsync("/api/maps/vector/tiles/mvt/0/0/0.pbf");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("application/vnd.mapbox-vector-tile", first.Content.Headers.ContentType?.MediaType);
        Assert.NotEmpty(await first.Content.ReadAsByteArrayAsync());
        Assert.Equal("false", first.Headers.GetValues("X-Tile-Cached").Single());

        using var second = await client.GetAsync("/api/maps/vector/tiles/mvt/0/0/0.pbf");
        Assert.Equal("true", second.Headers.GetValues("X-Tile-Cached").Single());
        Assert.Equal((await first.Content.ReadAsByteArrayAsync()), await second.Content.ReadAsByteArrayAsync());

        using var missing = await client.GetAsync("/api/maps/absent/tiles/mvt/0/0/0.pbf");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    private static readonly string[] TileServices = ["tiles"];

    public void Dispose()
    {
        _factory.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private sealed class VectorTileFactory(string directory) : SpatialHostFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            Directory.CreateDirectory(directory);
            builder.UseSetting("Spatial:Maps:Path", Path.Combine(directory, "maps.json"));
            builder.UseSetting("Spatial:Maps:LegacyPath", Path.Combine(directory, "legacy.json"));
            builder.UseSetting("Spatial:Admin:Token", "test-admin");
        }
    }
}
