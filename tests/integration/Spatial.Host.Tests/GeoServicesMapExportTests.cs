using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Spatial.Host.Tests;

/// <summary>
/// T-040 map export parity (research/compat/map-service.md §§1-2, T-E):
/// <c>time</c>/<c>timeRelation</c>/<c>layerTimeOptions</c> temporal filtering,
/// <c>dynamicLayers</c> per-request redefinition, <c>layerOption</c>, and
/// cached-root honesty (<c>singleFusedMapCache</c>/<c>tileInfo</c>/
/// <c>exportTilesAllowed</c> per served scheme). Red-first: export ignores
/// every one of these parameters today, and the root omits the advertisement
/// fields.
/// </summary>
public sealed class GeoServicesMapExportTests : IDisposable
{
    private static readonly string[] MapServices = ["map"];

    private const string Token = "test-admin-token";
    private const string Root = "/arcgis/rest/services";

    private const string BlueStyle =
        """[{"type":"circle","layout":{"visibility":"visible"},"paint":{"circle-color":"#0000ff","circle-radius":6,"circle-opacity":1.0}}]""";

    private const string RedDynamicLayers =
        """[{"id":0,"source":{"type":"mapLayer","mapLayerId":0},"drawingInfo":{"renderer":{"type":"simple","symbol":{"type":"esriSMS","style":"esriSMSCircle","color":[255,0,0,255],"size":12}}}}]""";

    private const string ExportPrefix =
        $"{Root}/world/MapServer/export?f=image&bbox=" + "-20,20,40,70&bboxSR=4326&imageSR=4326&size=100,75&format=png";

    private readonly string _directory = Directory.CreateTempSubdirectory("spatial-map-export-").FullName;
    private readonly WebApplicationFactory<Program> _factory;

    public GeoServicesMapExportTests() => _factory = new MapFactory(Path.Combine(_directory, "publications.json"));

    public void Dispose()
    {
        _factory.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private async Task<HttpClient> MapServiceAsync()
    {
        var client = _factory.CreateClient();
        var body = JsonSerializer.Serialize(new
        {
            name = "world",
            store = "demo",
            services = MapServices,
            layers = new[] { new { dataset = "demo.cities", layerId = 0, name = "Cities", style = BlueStyle } },
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

    private static async Task<byte[]> ImageAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        return await response.Content.ReadAsByteArrayAsync();
    }

    [Fact]
    public async Task Time_is_accepted_on_layers_without_date_fields()
    {
        var client = await MapServiceAsync();

        var image = await ImageAsync(await client.GetAsync(ExportPrefix + "&time=1199145600000"));

        Assert.NotEmpty(image);
    }

    [Fact]
    public async Task A_malformed_time_is_a_typed_error()
    {
        var client = await MapServiceAsync();

        var response = await client.GetAsync(ExportPrefix + "&time=yesterday");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(400, JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task An_unknown_time_relation_is_a_typed_error()
    {
        var client = await MapServiceAsync();

        var response = await client.GetAsync(ExportPrefix + "&time=1199145600000&timeRelation=esriTimeRelationFoo");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Layer_time_options_opting_out_are_accepted()
    {
        var client = await MapServiceAsync();

        var image = await ImageAsync(await client.GetAsync(
            ExportPrefix + "&time=1199145600000&layerTimeOptions=" +
            Uri.EscapeDataString("""[{"id":0,"useTime":false}]""")));

        Assert.NotEmpty(image);
    }

    [Fact]
    public async Task Malformed_layer_time_options_are_a_typed_error()
    {
        var client = await MapServiceAsync();

        var response = await client.GetAsync(
            ExportPrefix + "&layerTimeOptions=" + Uri.EscapeDataString("not-json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Dynamic_layers_redefine_the_rendered_style()
    {
        var client = await MapServiceAsync();

        var baseline = await ImageAsync(await client.GetAsync(ExportPrefix));
        var rerun = await ImageAsync(await client.GetAsync(ExportPrefix));
        Assert.Equal(baseline, rerun);

        var redefined = await ImageAsync(await client.GetAsync(
            ExportPrefix + "&dynamicLayers=" + Uri.EscapeDataString(RedDynamicLayers)));

        Assert.NotEqual(baseline, redefined);
    }

    [Fact]
    public async Task Malformed_dynamic_layers_are_a_typed_error()
    {
        var client = await MapServiceAsync();

        var response = await client.GetAsync(ExportPrefix + "&dynamicLayers=" + Uri.EscapeDataString("not-json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Dynamic_layers_naming_an_unknown_layer_are_not_found()
    {
        var client = await MapServiceAsync();

        var response = await client.GetAsync(
            ExportPrefix + "&dynamicLayers=" + Uri.EscapeDataString("""[{"id":9,"source":{"type":"mapLayer","mapLayerId":9}}]"""));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Layer_option_values_are_accepted()
    {
        var client = await MapServiceAsync();

        foreach (var option in new[] { "all", "visible", "top" })
        {
            var image = await ImageAsync(await client.GetAsync(ExportPrefix + $"&layerOption={option}"));
            Assert.NotEmpty(image);
        }
    }

    [Fact]
    public async Task An_unknown_layer_option_is_a_typed_error()
    {
        var client = await MapServiceAsync();

        var response = await client.GetAsync(ExportPrefix + "&layerOption=every");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_root_advertises_time_dynamic_layers_and_tile_honesty()
    {
        var client = await MapServiceAsync();

        var response = await client.GetAsync($"{Root}/world/MapServer?f=json");
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.True(root.GetProperty("supportsTimeRelation").GetBoolean());
        Assert.True(root.GetProperty("supportsDynamicLayers").GetBoolean());
        Assert.True(root.GetProperty("singleFusedMapCache").GetBoolean());
        Assert.False(root.GetProperty("exportTilesAllowed").GetBoolean());
        var lods = root.GetProperty("tileInfo").GetProperty("lods").EnumerateArray().ToArray();
        Assert.Equal(24, lods.Length);
        Assert.Equal(156543.03392800014, lods[0].GetProperty("resolution").GetDouble(), 1e-6);
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
