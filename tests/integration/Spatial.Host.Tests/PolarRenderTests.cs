using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.AspNetCore.Hosting;

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
/// <remarks>
/// One host for the class, one dataset and one map name per test (ADR-0160,
/// ADR-0161). The polar GeoJSON is ingested into the shared memory store, so
/// the dataset id — not the map name — is what keeps one test's band out of
/// another's tile.
/// </remarks>
public sealed class PolarRenderTests : IClassFixture<PolarRenderTests.PolarHost>
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

    private readonly PolarHost _host;

    public PolarRenderTests(PolarHost host) => _host = host;

    [Fact]
    public async Task Web_mercator_tiles_render_a_dataset_that_reaches_the_pole()
    {
        var (client, map) = await PublishAsync();

        var response = await client.GetAsync($"/api/maps/{map}/tiles/0/0/0.png");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task GeoServices_map_tiles_render_a_dataset_that_reaches_the_pole()
    {
        var (client, map) = await PublishAsync();

        var response = await client.GetAsync($"/arcgis/rest/services/{map}/MapServer/tile/0/0/0");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Wms_capabilities_clamp_the_mercator_bounding_box_to_the_projection_domain()
    {
        var (client, map) = await PublishAsync();

        var response = await client.GetAsync($"/ogc/{map}/wms?service=WMS&request=GetCapabilities");

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

    /// <summary>
    /// Ingests the polar band under a dataset id and publishes it as a map,
    /// both of them this test's own.
    /// </summary>
    private async Task<(HttpClient Client, string Map)> PublishAsync()
    {
        var client = _host.Client;
        var dataset = _host.NextDatasetName("test.polar");
        var map = _host.NextMapName("polar");
        var ingest = await client.SendAsync(Authorized(
            HttpMethod.Post,
            $"/api/ingest?store=memory&dataset={dataset}&srid=4326&format=geojson",
            new StringContent(PolarGeoJson, Encoding.UTF8, "application/geo+json")));
        Assert.Equal(HttpStatusCode.OK, ingest.StatusCode);

        var body = $$"""
            {"name":"{{map}}","store":"memory","services":["tiles","map","wms"],
             "layers":[{"dataset":"{{dataset}}","layerId":0,"name":"polar","style":{{JsonSerializer.Serialize(Style)}}}]}
            """;
        var put = await client.SendAsync(Authorized(
            HttpMethod.Put,
            $"/api/maps/{map}",
            new StringContent(body, Encoding.UTF8, "application/json")));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        return (client, map);
    }

    private static HttpRequestMessage Authorized(HttpMethod method, string path, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return request;
    }

    /// <summary>
    /// One host for the class, with an admin token and a per-class map file
    /// (ADR-0160). The dataset and the map name are the test's.
    /// </summary>
    public sealed class PolarHost : ClassHostFixture
    {
        public PolarHost()
            : base("spatial-polar")
        {
        }

        protected override void ConfigureHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Spatial:Admin:Token", Token);
            builder.UseSetting("Spatial:Maps:Path", MapsPath);
        }
    }
}
