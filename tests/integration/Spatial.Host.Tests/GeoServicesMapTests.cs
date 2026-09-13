using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Spatial.Adapter.GeoServices;
using Spatial.PluginSdk.Providers;

namespace Spatial.Host.Tests;

/// <summary>
/// The GeoServices MapServer facade (spec §4, ADR-0048): the root, layer
/// metadata (<c>drawingInfo</c>), all-layers, query, identify, find, export
/// and tiles, over a runtime <see cref="Spatial.PluginSdk.Providers.PublicationKind.Map"/>
/// publication backed by the demo store.
/// </summary>
public sealed class GeoServicesMapTests : IDisposable
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

    private const string PopulationClassBreaksStyle =
        """
        [
          {"type":"circle","filter":["all",[">=","population",0],["<","population",1000000]],"paint":{"circle-color":"#ffffcc","circle-radius":4}},
          {"type":"circle","filter":["all",[">=","population",1000000],["<","population",100000000]],"paint":{"circle-color":"#ff0000","circle-radius":8}}
        ]
        """;

    private const string LabelledStyle =
        """
        [
          {"type":"circle","paint":{"circle-color":"#ff0000","circle-radius":6}},
          {"type":"symbol","layout":{"text-field":["get","name"],"text-size":11},"paint":{"text-color":"#262626"}}
        ]
        """;

    private readonly string _directory = Directory.CreateTempSubdirectory("spatial-map-").FullName;
    private readonly WebApplicationFactory<Program> _factory;

    public GeoServicesMapTests() => _factory = new MapFactory(Path.Combine(_directory, "publications.json"));

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

    [Fact]
    public async Task The_catalog_advertises_the_map_service()
    {
        var client = await MapServiceAsync();

        var services = (await BodyAsync(await client.GetAsync($"{Root}?f=json"))).GetProperty("services").EnumerateArray()
            .Select(service => (service.GetProperty("name").GetString(), service.GetProperty("type").GetString()))
            .ToArray();

        Assert.Contains(("world", "MapServer"), services);
    }

    [Fact]
    public async Task The_root_describes_the_map_and_its_layers()
    {
        var client = await MapServiceAsync();

        var root = await BodyAsync(await client.GetAsync($"{Root}/world/MapServer?f=json"));

        Assert.Equal("Map,Query,Data", root.GetProperty("capabilities").GetString());
        Assert.True(root.GetProperty("singleFusedMapCache").GetBoolean());
        Assert.Equal(4326, root.GetProperty("spatialReference").GetProperty("wkid").GetInt32());
        Assert.True(root.GetProperty("tileInfo").GetProperty("lods").GetArrayLength() > 0);
        Assert.True(root.GetProperty("fullExtent").GetProperty("xmax").GetDouble() > 0);
        Assert.Equal(0, root.GetProperty("layers")[0].GetProperty("id").GetInt32());
        Assert.Equal("Cities", root.GetProperty("layers")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task The_layer_metadata_carries_the_projected_drawing_info()
    {
        var client = await MapServiceAsync();

        var layer = await BodyAsync(await client.GetAsync($"{Root}/world/MapServer/0?f=json"));

        Assert.Equal("esriGeometryPoint", layer.GetProperty("geometryType").GetString());
        var renderer = layer.GetProperty("drawingInfo").GetProperty("renderer");
        Assert.Equal("simple", renderer.GetProperty("type").GetString());
        var symbol = renderer.GetProperty("symbol");
        Assert.Equal("esriSMS", symbol.GetProperty("type").GetString());
        var color = symbol.GetProperty("color").EnumerateArray().Select(channel => channel.GetInt32()).ToArray();
        Assert.Equal([255, 0, 0, 255], color);
    }

    [Fact]
    public async Task All_layers_lists_the_published_layers()
    {
        var client = await MapServiceAsync();

        var layers = await BodyAsync(await client.GetAsync($"{Root}/world/MapServer/layers?f=json"));

        Assert.Single(layers.GetProperty("layers").EnumerateArray());
    }

    [Fact]
    public async Task Query_counts_the_layer_features()
    {
        var client = await MapServiceAsync();

        var response = await client.PostAsync(
            $"{Root}/world/MapServer/0/query",
            new FormUrlEncodedContent([new KeyValuePair<string, string>("returnCountOnly", "true"), new KeyValuePair<string, string>("f", "json")]));

        Assert.True((await BodyAsync(response)).GetProperty("count").GetInt32() > 0);
    }

    [Fact]
    public async Task Identify_finds_the_city_under_a_point()
    {
        var client = await MapServiceAsync();

        var identify = await BodyAsync(await client.GetAsync(
            $"{Root}/world/MapServer/identify?f=json" +
            "&geometry=" + Uri.EscapeDataString("""{"x":13.405,"y":52.52,"spatialReference":{"wkid":4326}}""") +
            "&geometryType=esriGeometryPoint&sr=4326&tolerance=5&layers=all" +
            "&mapExtent=" + Uri.EscapeDataString("-20,20,40,70") + "&imageDisplay=" + Uri.EscapeDataString("400,300,96")));

        var results = identify.GetProperty("results").EnumerateArray().ToArray();
        Assert.NotEmpty(results);
        Assert.Equal(0, results[0].GetProperty("layerId").GetInt32());
        Assert.Equal("Berlin", results[0].GetProperty("value").GetString());
    }

    [Fact]
    public async Task Find_matches_text_over_the_string_fields()
    {
        var client = await MapServiceAsync();

        var find = await BodyAsync(await client.GetAsync(
            $"{Root}/world/MapServer/find?f=json&searchText=Ber&layers=0&returnGeometry=false"));

        var results = find.GetProperty("results").EnumerateArray().ToArray();
        Assert.NotEmpty(results);
        Assert.Equal("name", results[0].GetProperty("foundFieldName").GetString());
        Assert.Equal("Berlin", results[0].GetProperty("value").GetString());
    }

    [Fact]
    public async Task Find_projects_geometry_when_a_different_sr_is_requested()
    {
        var client = await MapServiceAsync();

        var find = await BodyAsync(await client.GetAsync(
            $"{Root}/world/MapServer/find?f=json&searchText=Ber&layers=0&returnGeometry=true&sr=3857"));

        var geometry = find.GetProperty("results").EnumerateArray().First().GetProperty("geometry");
        Assert.True(geometry.GetProperty("x").GetDouble() > 1_000_000);
    }

    [Fact]
    public async Task Find_keeps_the_layer_geometry_when_the_requested_sr_matches()
    {
        var client = await MapServiceAsync();

        var find = await BodyAsync(await client.GetAsync(
            $"{Root}/world/MapServer/find?f=json&searchText=Ber&layers=0&returnGeometry=true&sr=4326"));

        var geometry = find.GetProperty("results").EnumerateArray().First().GetProperty("geometry");
        Assert.NotEqual(0, geometry.GetProperty("x").GetDouble());
    }

    [Theory]
    [InlineData("name", true)]
    [InlineData("name,population", true)]
    [InlineData("population", false)]
    [InlineData("missing", false)]
    public async Task Find_honours_the_search_fields_filter(string searchFields, bool matches)
    {
        var client = await MapServiceAsync();

        var find = await BodyAsync(await client.GetAsync(
            $"{Root}/world/MapServer/find?f=json&searchText=Ber&layers=0&returnGeometry=false&searchFields={searchFields}"));

        Assert.Equal(matches, find.GetProperty("results").EnumerateArray().Any());
    }

    [Fact]
    public async Task Identify_without_a_map_extent_uses_an_exact_intersection()
    {
        var client = await MapServiceAsync();

        var identify = await BodyAsync(await client.GetAsync(
            $"{Root}/world/MapServer/identify?f=json" +
            "&geometry=" + Uri.EscapeDataString("""{"x":13.405,"y":52.52,"spatialReference":{"wkid":4326}}""") +
            "&geometryType=esriGeometryPoint&sr=4326&layers=all"));

        Assert.True(identify.GetProperty("results").EnumerateArray().Any());
    }

    [Fact]
    public async Task Identify_with_a_zero_width_display_uses_an_exact_intersection()
    {
        var client = await MapServiceAsync();

        var identify = await BodyAsync(await client.GetAsync(
            $"{Root}/world/MapServer/identify?f=json" +
            "&geometry=" + Uri.EscapeDataString("""{"x":13.405,"y":52.52,"spatialReference":{"wkid":4326}}""") +
            "&geometryType=esriGeometryPoint&sr=4326&tolerance=5&layers=all" +
            "&mapExtent=" + Uri.EscapeDataString("-20,20,40,70") + "&imageDisplay=" + Uri.EscapeDataString("0,300,96")));

        Assert.True(identify.GetProperty("results").EnumerateArray().Any());
    }

    [Fact]
    public async Task Export_streams_an_image_for_f_image()
    {
        var client = await MapServiceAsync();

        var response = await client.GetAsync(
            $"{Root}/world/MapServer/export?f=image&bbox=" + Uri.EscapeDataString("-20,20,40,70") +
            "&bboxSR=4326&imageSR=4326&size=200,150&format=png&transparent=true");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("200", response.Headers.GetValues("X-Raster-Width").Single());
    }

    [Fact]
    public async Task Export_json_returns_an_image_href()
    {
        var client = await MapServiceAsync();

        var export = await BodyAsync(await client.GetAsync(
            $"{Root}/world/MapServer/export?f=json&bbox=" + Uri.EscapeDataString("-20,20,40,70") +
            "&bboxSR=4326&imageSR=4326&size=200,150&format=png"));

        Assert.Equal(200, export.GetProperty("width").GetInt32());
        Assert.Contains("f=image", export.GetProperty("href").GetString());
    }

    [Fact]
    public async Task A_tile_renders_through_the_scheme()
    {
        var client = await MapServiceAsync();

        var response = await client.GetAsync($"{Root}/world/MapServer/tile/0/0/0?f=image");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task The_layer_metadata_carries_a_unique_value_renderer_and_coded_domain()
    {
        var client = await MapServiceAsync(CountryUniqueValueStyle, dataset: "demo.world_cities");

        var layer = await BodyAsync(await client.GetAsync($"{Root}/world/MapServer/0?f=json"));

        var renderer = layer.GetProperty("drawingInfo").GetProperty("renderer");
        Assert.Equal("uniqueValue", renderer.GetProperty("type").GetString());
        Assert.Equal("country", renderer.GetProperty("field1").GetString());
        Assert.Equal(
            ["Germany", "France"],
            renderer.GetProperty("uniqueValueInfos").EnumerateArray().Select(info => info.GetProperty("value").GetString()));

        var domain = layer.GetProperty("domains").GetProperty("country");
        Assert.Equal("codedValue", domain.GetProperty("type").GetString());
        Assert.Equal(2, domain.GetProperty("codedValues").GetArrayLength());
        Assert.Equal(
            "codedValue",
            layer.GetProperty("fields").EnumerateArray()
                .Single(field => field.GetProperty("name").GetString() == "country")
                .GetProperty("domain").GetProperty("type").GetString());
    }

    [Fact]
    public async Task The_layer_metadata_carries_a_class_breaks_renderer_and_range_domain()
    {
        var client = await MapServiceAsync(PopulationClassBreaksStyle);

        var layer = await BodyAsync(await client.GetAsync($"{Root}/world/MapServer/0?f=json"));

        var renderer = layer.GetProperty("drawingInfo").GetProperty("renderer");
        Assert.Equal("classBreaks", renderer.GetProperty("type").GetString());
        Assert.Equal("population", renderer.GetProperty("field").GetString());
        Assert.Equal(0, renderer.GetProperty("minValue").GetDouble());
        Assert.Equal(
            [1000000, 100000000],
            renderer.GetProperty("classBreakInfos").EnumerateArray().Select(info => info.GetProperty("classMaxValue").GetDouble()));

        var range = layer.GetProperty("domains").GetProperty("population").GetProperty("range");
        Assert.Equal(0, range[0].GetDouble());
        Assert.Equal(100000000, range[1].GetDouble());
    }

    [Fact]
    public async Task The_layer_metadata_carries_labeling_info()
    {
        var client = await MapServiceAsync(LabelledStyle);

        var layer = await BodyAsync(await client.GetAsync($"{Root}/world/MapServer/0?f=json"));

        var label = layer.GetProperty("drawingInfo").GetProperty("labelingInfo")[0];
        Assert.Equal("[name]", label.GetProperty("labelExpression").GetString());
        Assert.Equal("esriServerPointLabelPlacementCenterCenter", label.GetProperty("labelPlacement").GetString());
        Assert.Equal("esriTS", label.GetProperty("symbol").GetProperty("type").GetString());
        Assert.Equal(11, label.GetProperty("symbol").GetProperty("font").GetProperty("size").GetDouble());
    }

    [Fact]
    public async Task The_map_server_image_resource_reports_a_typed_not_found()
    {
        var client = await MapServiceAsync();

        var response = await client.GetAsync($"{Root}/world/MapServer/0/images/1DD4FC53?f=json");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(404, error.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Contains("picture", error.GetProperty("error").GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Reading_map_layers_honours_cancellation()
    {
        var store = new WritableMemoryStore();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MapServerResources.ReadLayersAsync(
            store, store, [new PublishedLayer(0, "memory.places", "Places")], new CancellationToken(canceled: true)));
    }

    [Fact]
    public async Task A_feature_server_route_does_not_serve_a_map_publication()
    {
        var client = await MapServiceAsync();

        var response = await client.GetAsync($"{Root}/world/FeatureServer?f=json");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
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
