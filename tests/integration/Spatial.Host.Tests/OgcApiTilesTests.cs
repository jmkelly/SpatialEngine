using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Spatial.Host.Tests;

/// <summary>
/// OGC API Tiles over the live MVT contract (ADR-0070): a map's tile landing
/// page, collection tile resource, TileJSON and negotiated MVT tile data all
/// describe the same tile set and cache bytes as the neutral vector route.
/// </summary>
public sealed class OgcApiTilesTests : IDisposable
{
    private static readonly object[] MapLayers = [new { dataset = "demo.cities", layerId = 0, name = "cities" }];
    private static readonly string[] TileServices = ["tiles"];

    private readonly string _directory = Directory.CreateTempSubdirectory("spatial-ogc-tiles-").FullName;
    private readonly WebApplicationFactory<Program> _factory;

    public OgcApiTilesTests() => _factory = new Factory(Path.Combine(_directory, "maps.json"));

    [Fact]
    public async Task Landing_collection_and_tilejson_links_describe_the_map_tile_set()
    {
        using var client = await MapAsync("world");

        using var landing = await GetJsonAsync(client, "/ogc/world/tiles");
        Assert.Equal(HttpStatusCode.OK, landing.StatusCode);
        Assert.Equal("application/json", landing.Content.Headers.ContentType?.MediaType);
        var landingJson = JsonDocument.Parse(await landing.Content.ReadAsStringAsync()).RootElement;
        Assert.EndsWith("/ogc/world/tiles", Link(landingJson, "self"));
        Assert.EndsWith("/ogc/world/tiles/collections", Link(landingJson, "http://www.opengis.net/def/rel/ogc/1.0/collections"));

        using var collections = await GetJsonAsync(client, "/ogc/world/tiles/collections");
        Assert.Equal(HttpStatusCode.OK, collections.StatusCode);
        var collection = JsonDocument.Parse(await collections.Content.ReadAsStringAsync()).RootElement
            .GetProperty("collections")[0];
        Assert.EndsWith("/ogc/world/tiles/collections/world", Link(collection, "item"));

        using var tileJsonResponse = await GetJsonAsync(client,
            "/ogc/world/tiles/collections/world/tiles/WebMercatorQuad/TileJSON");
        Assert.Equal(HttpStatusCode.OK, tileJsonResponse.StatusCode);
        var tileJson = JsonDocument.Parse(await tileJsonResponse.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("2.2.0", tileJson.GetProperty("tilejson").GetString());
        Assert.Equal("xyz", tileJson.GetProperty("scheme").GetString());
        Assert.Equal(0, tileJson.GetProperty("minzoom").GetInt32());
        Assert.Equal(23, tileJson.GetProperty("maxzoom").GetInt32());
        Assert.Equal("cities", tileJson.GetProperty("vector_layers")[0].GetProperty("id").GetString());
        Assert.Contains("/ogc/world/tiles/collections/world/tiles/WebMercatorQuad/{z}/{x}/{y}.pbf", tileJson.GetProperty("tiles")[0].GetString());
    }

    [Fact]
    public async Task Collection_tiles_links_and_tile_data_negotiate_mvt()
    {
        using var client = await MapAsync("world");
        using var resource = await GetJsonAsync(client, "/ogc/world/tiles/collections/world/tiles");
        Assert.Equal(HttpStatusCode.OK, resource.StatusCode);
        var json = JsonDocument.Parse(await resource.Content.ReadAsStringAsync()).RootElement;
        Assert.EndsWith("/ogc/world/tiles/collections/world/tiles", Link(json, "self"));
        Assert.EndsWith("/ogc/world/tiles/collections/world/tiles/WebMercatorQuad/TileJSON", Link(json, "http://www.opengis.net/def/rel/ogc/1.0/tilejson"));

        using var request = new HttpRequestMessage(HttpMethod.Get,
            "/ogc/world/tiles/collections/world/tiles/WebMercatorQuad/0/0/0.pbf");
        request.Headers.Accept.ParseAdd("application/vnd.mapbox-vector-tile");
        using var tile = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, tile.StatusCode);
        Assert.Equal("application/vnd.mapbox-vector-tile", tile.Content.Headers.ContentType?.MediaType);
        Assert.NotEmpty(await tile.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Tile_endpoints_reject_bad_negotiation_and_addresses_as_ogc_failures()
    {
        using var client = await MapAsync("world");
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "/ogc/world/tiles/collections/world/tiles/WebMercatorQuad/0/0/0.pbf");
        request.Headers.Accept.ParseAdd("text/html");
        using var format = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, format.StatusCode);
        Assert.Equal("InvalidFormat", await ReportCodeAsync(format));

        using var address = await GetMvtAsync(client,
            "/ogc/world/tiles/collections/world/tiles/WebMercatorQuad/25/0/0.pbf");
        Assert.Equal(HttpStatusCode.BadRequest, address.StatusCode);
        Assert.Equal("InvalidParameterValue", await ReportCodeAsync(address));
    }

    [Fact]
    public async Task Missing_map_is_a_structured_ogc_not_found_failure()
    {
        using var client = _factory.CreateClient();
        using var response = await GetJsonAsync(client, "/ogc/absent/tiles");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("LayerNotDefined", await ReportCodeAsync(response));
    }

    private static async Task<HttpResponseMessage> GetMvtAsync(HttpClient client, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Accept.ParseAdd("application/vnd.mapbox-vector-tile");
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> GetJsonAsync(HttpClient client, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Accept.ParseAdd("application/json");
        return await client.SendAsync(request);
    }

    private static string Link(JsonElement element, string rel) => element.GetProperty("links")
        .EnumerateArray().Single(link => link.GetProperty("rel").GetString() == rel)
        .GetProperty("href").GetString()!;

    private static async Task<string> ReportCodeAsync(HttpResponseMessage response)
    {
        var document = System.Xml.Linq.XDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.Descendants().Single(element => element.Name.LocalName == "ServiceException")
            .Attribute("code")!.Value;
    }

    private async Task<HttpClient> MapAsync(string name)
    {
        var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/maps/{name}")
        {
            Content = JsonContent.Create(new
            {
                name,
                store = "demo",
                layers = MapLayers,
                services = TileServices,
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "test-admin");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    public void Dispose()
    {
        _factory.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private sealed class Factory(string mapsPath) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Spatial:Maps:Path", mapsPath);
            builder.UseSetting("Spatial:Maps:LegacyPath", Path.Combine(Path.GetDirectoryName(mapsPath)!, "legacy.json"));
            builder.UseSetting("Spatial:Admin:Token", "test-admin");
        }
    }
}
