using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Spatial.Host.Tests;

/// <summary>
/// A global dataset whose extent reaches a pole (latitude ±90) is valid in
/// EPSG:4326 but falls outside Web Mercator's projected domain. Rendering it
/// through the Web-Mercator tile routes and describing it through WMS must
/// still succeed: the render pipeline clips source geometry to the viewport's
/// source envelope before reprojection, and the WMS capabilities clamp the
/// mercator bounding box to the projection's domain. Before that, both fail
/// with "Transformation cannot be computed at the poles."
/// </summary>
public sealed class PolarRenderTests : IDisposable
{
    private const string Token = "test-admin-token";

    private static readonly XNamespace Wms = "http://www.opengis.net/wms";

    /// <summary>A band from the south pole to 60°S: valid geography, outside Web Mercator.</summary>
    private const string PolarGeoJson = """
        {"type":"FeatureCollection","features":[
          {"type":"Feature","geometry":{"type":"Polygon","coordinates":[[[-180,-90],[180,-90],[180,-60],[-180,-60],[-180,-90]]]},"properties":{"name":"Polar band"}}
        ]}
        """;

    private const string Style =
        """[{"type":"fill","layout":{"visibility":"visible"},"paint":{"fill-color":"#3366ff","fill-opacity":1.0}}]""";

    private readonly string _directory = Directory.CreateTempSubdirectory("spatial-polar-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private WebApplicationFactory<Program> Factory() => new PolarFactory(Path.Combine(_directory, "maps.json"));

    [Fact]
    public async Task Web_mercator_tiles_render_a_dataset_that_reaches_the_pole()
    {
        using var factory = Factory();
        var client = await PublishAsync(factory);

        var response = await client.GetAsync("/api/maps/polar/tiles/0/0/0.png");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task GeoServices_map_tiles_render_a_dataset_that_reaches_the_pole()
    {
        using var factory = Factory();
        var client = await PublishAsync(factory);

        var response = await client.GetAsync("/arcgis/rest/services/polar/MapServer/tile/0/0/0");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Wms_capabilities_clamp_the_mercator_bounding_box_to_the_projection_domain()
    {
        using var factory = Factory();
        var client = await PublishAsync(factory);

        var response = await client.GetAsync("/ogc/polar/wms?service=WMS&request=GetCapabilities");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var document = XDocument.Parse(await response.Content.ReadAsStringAsync());
        var mercator = document.Descendants(Wms + "Layer")
            .SelectMany(layer => layer.Elements(Wms + "BoundingBox"))
            .Single(box => box.Attribute("CRS")?.Value == "EPSG:3857");
        Assert.True(
            double.Parse(mercator.Attribute("miny")!.Value, CultureInfo.InvariantCulture) >= -20_037_508.35
            && double.Parse(mercator.Attribute("maxy")!.Value, CultureInfo.InvariantCulture) <= 20_037_508.35,
            $"the mercator bounding box must stay inside the projection's domain, got {mercator}.");
    }

    private static async Task<HttpClient> PublishAsync(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        var ingest = await client.SendAsync(Authorized(
            HttpMethod.Post,
            "/api/ingest?store=memory&dataset=test.polar&srid=4326&format=geojson&identity=none",
            new StringContent(PolarGeoJson, Encoding.UTF8, "application/geo+json")));
        Assert.Equal(HttpStatusCode.OK, ingest.StatusCode);

        var body = $$"""
            {"name":"polar","store":"memory","services":["tiles","map","wms"],
             "layers":[{"dataset":"test.polar","layerId":0,"name":"polar","style":{{JsonSerializer.Serialize(Style)}}}]}
            """;
        var put = await client.SendAsync(Authorized(
            HttpMethod.Put,
            "/api/maps/polar",
            new StringContent(body, Encoding.UTF8, "application/json")));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        return client;
    }

    private static HttpRequestMessage Authorized(HttpMethod method, string path, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return request;
    }

    /// <summary>A host with an admin token, a per-test map file and the ephemeral memory store.</summary>
    private sealed class PolarFactory(string mapsPath) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Spatial:Admin:Token", Token);
            builder.UseSetting("Spatial:Maps:Path", mapsPath);
        }
    }
}
