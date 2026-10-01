using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace Spatial.Host.Tests;

/// <summary>
/// The host discovery page (ADR-0018): <c>GET /routes</c> is an HTML index of
/// every mounted route and every published map's exposed services, read live
/// from the routing table and the map registry.
/// </summary>
/// <remarks>
/// One host for the class, one map name per test (ADR-0160, ADR-0161): the
/// page is read for the map the test itself published, and the negative
/// assertions name that map rather than any name at all.
/// </remarks>
public sealed class DiscoveryPageTests : IClassFixture<DiscoveryPageTests.RoutesHost>
{
    private const string Token = "test-admin-token";

    private const string Style =
        """[{"type":"circle","layout":{"visibility":"visible"},"paint":{"circle-color":"#ff0000","circle-radius":6}}]""";

    private readonly RoutesHost _host;

    public DiscoveryPageTests(RoutesHost host) => _host = host;

    [Fact]
    public async Task Routes_page_is_html_and_lists_the_mounted_endpoints()
    {
        var client = _host.Client;

        var response = await client.GetAsync("/routes");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("/api/maps", body);
        Assert.Contains("/arcgis/rest/services", body);
        Assert.Contains("/ogc/{name}/wms", body);
        Assert.Contains("Engine API", body);
        Assert.Contains("OpenAPI", body);
    }

    [Fact]
    public async Task Routes_page_links_each_published_map_service()
    {
        var world = _host.NextMapName("world");
        var client = _host.Client;
        await PublishAsync(client, world, ["feature", "map", "tiles", "wms", "wfs"]);

        var body = await (await client.GetAsync("/routes")).Content.ReadAsStringAsync();

        Assert.Contains($"/arcgis/rest/services/{world}/FeatureServer", body);
        Assert.Contains($"/arcgis/rest/services/{world}/MapServer", body);
        Assert.Contains($"/ogc/{world}/wms", body);
        Assert.Contains($"/ogc/{world}/wfs", body);
        Assert.Contains($"/api/maps/{world}/tiles/{{z}}/{{x}}/{{y}}.png", body);
        Assert.Contains("request=GetMap", body);
    }

    [Fact]
    public async Task Routes_page_does_not_link_a_service_the_map_does_not_expose()
    {
        var draft = _host.NextMapName("draft");
        var client = _host.Client;
        await PublishAsync(client, draft, ["feature"]);

        var body = await (await client.GetAsync("/routes")).Content.ReadAsStringAsync();

        Assert.Contains($"/arcgis/rest/services/{draft}/FeatureServer", body);
        Assert.DoesNotContain($"/arcgis/rest/services/{draft}/MapServer", body);
        Assert.DoesNotContain($"/ogc/{draft}/wms", body);
    }

    [Fact]
    public async Task Root_advertises_the_routes_page()
    {
        var client = _host.Client;

        var body = await (await client.GetAsync("/")).Content.ReadAsStringAsync();

        Assert.Contains("/routes", body);
    }

    private static async Task PublishAsync(HttpClient client, string name, string[] services)
    {
        var body = JsonSerializer.Serialize(new
        {
            name,
            store = "demo",
            services,
            layers = new[] { new { dataset = "demo.cities", layerId = 0, name = "cities", style = Style } },
        });
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/maps/{name}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// One host for the class, with an admin token and a per-class map file
    /// (ADR-0160). The map name is the test's.
    /// </summary>
    public sealed class RoutesHost : ClassHostFixture
    {
        public RoutesHost()
            : base("spatial-routes")
        {
        }

        protected override void ConfigureHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Spatial:Admin:Token", Token);
            builder.UseSetting("Spatial:Maps:Path", MapsPath);
        }
    }
}
