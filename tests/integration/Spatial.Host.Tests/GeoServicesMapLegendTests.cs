using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Spatial.Host.Tests;

/// <summary>
/// The MapServer legend family (research/compat/map-service.md §1 T-D,
/// S4 legend-map-service/): <c>legend</c>, <c>queryDomains</c>,
/// <c>queryLegends</c> and per-layer <c>generateRenderer</c>. Red-first:
/// these replay the <c>research/compat/ground-truth/map-legend.Census.json</c>
/// byte-shape (per-layer legends whose swatches decode as PNG) and fail
/// while the routes are unmounted.
/// </summary>
public sealed class GeoServicesMapLegendTests : IDisposable
{
    private static readonly string[] MapServices = ["map"];

    private const string Token = "test-admin-token";
    private const string Root = "/arcgis/rest/services";

    private const string CityStyle =
        """[{"type":"circle","layout":{"visibility":"visible"},"paint":{"circle-color":"#ff0000","circle-radius":6,"circle-opacity":1.0}}]""";

    private const string CountryUniqueValueStyle =
        """
        [
          {"type":"circle","filter":["==","country","Germany"],"paint":{"circle-color":"#ff0000","circle-radius":6}},
          {"type":"circle","filter":["==","country","France"],"paint":{"circle-color":"#0000ff","circle-radius":6}}
        ]
        """;

    private readonly string _directory = Directory.CreateTempSubdirectory("spatial-map-legend-").FullName;
    private readonly WebApplicationFactory<Program> _factory;

    public GeoServicesMapLegendTests() => _factory = new MapFactory(Path.Combine(_directory, "publications.json"));

    public void Dispose()
    {
        _factory.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private async Task<HttpClient> MapServiceAsync(string style = CityStyle, string dataset = "demo.cities")
    {
        var client = _factory.CreateClient();
        var body = JsonSerializer.Serialize(new
        {
            name = "world",
            store = "demo",
            services = MapServices,
            layers = new[] { new { dataset, layerId = 0, name = "Cities", style } },
        });
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/maps/world")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    /// <summary>
    /// The G1 replay shape: every layer carries legend entries whose
    /// <c>imageData</c> decodes to PNG bytes, as in
    /// <c>map-legend.Census.json</c> (which carries 4 layers, the first with
    /// 5 class-break swatches and the rest with a single swatch).
    /// </summary>
    [Fact]
    public async Task Legend_returns_per_layer_legends_with_png_swatch_bytes()
    {
        var client = await MapServiceAsync();

        var legend = await BodyAsync(await client.GetAsync($"{Root}/world/MapServer/legend?f=json"));

        var layers = legend.GetProperty("layers").EnumerateArray().ToArray();
        var layer = Assert.Single(layers);
        Assert.Equal(0, layer.GetProperty("layerId").GetInt32());
        Assert.Equal("Cities", layer.GetProperty("layerName").GetString());
        var entries = layer.GetProperty("legend").EnumerateArray().ToArray();
        Assert.NotEmpty(entries);
        foreach (var entry in entries)
        {
            Assert.Equal("image/png", entry.GetProperty("contentType").GetString());
            Assert.True(entry.GetProperty("width").GetInt32() > 0);
            Assert.True(entry.GetProperty("height").GetInt32() > 0);
            var bytes = Convert.FromBase64String(entry.GetProperty("imageData").GetString()!);
            Assert.Equal([0x89, 0x50, 0x4E, 0x47], bytes[..4]);
        }
    }

    [Fact]
    public async Task Legend_projects_one_entry_per_unique_value()
    {
        var client = await MapServiceAsync(CountryUniqueValueStyle, dataset: "demo.world_cities");

        var legend = await BodyAsync(await client.GetAsync($"{Root}/world/MapServer/legend?f=json"));

        var entries = legend.GetProperty("layers").EnumerateArray().Single()
            .GetProperty("legend").EnumerateArray().ToArray();
        Assert.Equal(2, entries.Length);
        Assert.Equal(["France", "Germany"], entries.Select(entry => entry.GetProperty("label").GetString()).Order());
    }

    [Fact]
    public async Task Query_domains_returns_the_rendered_field_domain()
    {
        var client = await MapServiceAsync(CountryUniqueValueStyle, dataset: "demo.world_cities");

        var domains = await BodyAsync(await client.GetAsync($"{Root}/world/MapServer/queryDomains?f=json&layers=0"));

        var layer = domains.GetProperty("domains").EnumerateArray().Single();
        Assert.Equal(0, layer.GetProperty("layerId").GetInt32());
        Assert.Equal("codedValue", layer.GetProperty("domains").GetProperty("country").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Query_legends_filters_to_the_requested_layers()
    {
        var client = await MapServiceAsync();

        var legends = await BodyAsync(await client.GetAsync($"{Root}/world/MapServer/queryLegends?f=json&layers=0"));

        var layers = legends.GetProperty("layers").EnumerateArray().ToArray();
        Assert.Single(layers);
        Assert.Equal(0, layers[0].GetProperty("layerId").GetInt32());
        Assert.NotEmpty(layers[0].GetProperty("legend").EnumerateArray());
    }

    [Fact]
    public async Task Generate_renderer_classifies_equal_interval_breaks()
    {
        var client = await MapServiceAsync();
        var classification = Uri.EscapeDataString(
            """{"type":"classBreaksDef","classificationField":"population","breakCount":2,"classificationMethod":"esriClassifyEqualInterval"}""");

        var renderer = await BodyAsync(await client.GetAsync(
            $"{Root}/world/MapServer/0/generateRenderer?f=json&classificationDef={classification}"));

        var body = renderer.GetProperty("renderer");
        Assert.Equal("classBreaks", body.GetProperty("type").GetString());
        Assert.Equal("population", body.GetProperty("field").GetString());
        Assert.Equal(2, body.GetProperty("classBreakInfos").GetArrayLength());
    }

    [Fact]
    public async Task Generate_renderer_enumerates_unique_values()
    {
        var client = await MapServiceAsync(style: CityStyle, dataset: "demo.world_cities");
        var classification = Uri.EscapeDataString(
            """{"type":"uniqueValueDef","uniqueValueFields":["country"]}""");

        var renderer = await BodyAsync(await client.GetAsync(
            $"{Root}/world/MapServer/0/generateRenderer?f=json&classificationDef={classification}&where=" +
            Uri.EscapeDataString("country = 'DE' OR country = 'FR'")));

        var body = renderer.GetProperty("renderer");
        Assert.Equal("uniqueValue", body.GetProperty("type").GetString());
        Assert.Equal("country", body.GetProperty("field1").GetString());
        Assert.Equal(2, body.GetProperty("uniqueValueInfos").GetArrayLength());
    }

    [Fact]
    public async Task Generate_renderer_without_a_classification_is_a_typed_error()
    {
        var client = await MapServiceAsync();

        var response = await client.GetAsync($"{Root}/world/MapServer/0/generateRenderer?f=json");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(400, JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Generate_renderer_for_an_unknown_layer_is_not_found()
    {
        var client = await MapServiceAsync();
        var classification = Uri.EscapeDataString(
            """{"type":"classBreaksDef","classificationField":"population","breakCount":2}""");

        var response = await client.GetAsync(
            $"{Root}/world/MapServer/99/generateRenderer?f=json&classificationDef={classification}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// T-085: swatch <c>url</c> tokens must be deterministic across host
    /// instances (content hash, not a per-process <c>HashCode</c>) so legend
    /// responses are cacheable and replay-stable across restarts.
    /// </summary>
    [Fact]
    public async Task Legend_swatch_urls_are_identical_across_factory_instances()
    {
        static async Task<string> LegendUrlAsync()
        {
            var directory = Directory.CreateTempSubdirectory("spatial-map-legend-t085-").FullName;
            try
            {
                using var factory = new MapFactory(Path.Combine(directory, "publications.json"));
                var client = factory.CreateClient();
                var body = JsonSerializer.Serialize(new
                {
                    name = "world",
                    store = "demo",
                    services = MapServices,
                    layers = new[] { new { dataset = "demo.cities", layerId = 0, name = "Cities", style = CityStyle } },
                });
                using var request = new HttpRequestMessage(HttpMethod.Put, "/api/maps/world")
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
                Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request)).StatusCode);

                var legend = await BodyAsync(await client.GetAsync($"{Root}/world/MapServer/legend?f=json"));
                var url = legend.GetProperty("layers").EnumerateArray().Single()
                    .GetProperty("legend").EnumerateArray().Single()
                    .GetProperty("url").GetString()!;
                Assert.Matches("^[0-9a-f]{32}$", url);
                return url;
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        Assert.Equal(await LegendUrlAsync(), await LegendUrlAsync());
    }

    private sealed class MapFactory(string publicationsPath) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Spatial:Admin:Token", Token);
            builder.UseSetting("Spatial:Maps:Path", publicationsPath);
        }
    }
}
