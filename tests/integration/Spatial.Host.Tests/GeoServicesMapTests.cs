using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Spatial.Adapter.GeoServices;
using Spatial.Contracts.Providers;

namespace Spatial.Host.Tests;

/// <summary>
/// The GeoServices MapServer facade (spec §4, ADR-0048): the root, layer
/// metadata (<c>drawingInfo</c>), all-layers, query, identify, find, export
/// and tiles, over a runtime <see cref="Spatial.Contracts.Providers.PublicationKind.Map"/>
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
    private readonly SpatialHostFactory _factory;

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
    public async Task Html_on_the_map_server_root_is_rejected_naming_the_json_surface()
    {
        var client = await MapServiceAsync();

        var response = await client.GetAsync($"{Root}/world/MapServer?f=html");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("error");
        Assert.Equal(400, error.GetProperty("code").GetInt32());
        var message = error.GetProperty("message").GetString() ?? string.Empty;
        Assert.Contains("supportedQueryFormats", message, StringComparison.Ordinal);
        Assert.Contains("f=json", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_root_describes_the_map_and_its_layers()
    {
        var client = await MapServiceAsync();

        var root = await BodyAsync(await client.GetAsync($"{Root}/world/MapServer?f=json"));

        Assert.Equal("Map,Query,Data", root.GetProperty("capabilities").GetString());
        Assert.True(root.GetProperty("singleFusedMapCache").GetBoolean());
        // A fused-cache root is a tile-matrix document: the service reference
        // follows the tile scheme (3857), not the data CRS, or tile clients
        // derive Null-Island indices for a real canvas.
        Assert.Equal(3857, root.GetProperty("spatialReference").GetProperty("wkid").GetInt32());
        Assert.Equal("esriMeters", root.GetProperty("units").GetString());
        Assert.Equal(3857, root.GetProperty("fullExtent").GetProperty("spatialReference").GetProperty("wkid").GetInt32());
        Assert.True(root.GetProperty("fullExtent").GetProperty("xmax").GetDouble() > 1_000_000);
        Assert.True(root.GetProperty("tileInfo").GetProperty("lods").GetArrayLength() > 0);
        Assert.True(root.GetProperty("fullExtent").GetProperty("xmax").GetDouble() > 0);
        Assert.Equal(0, root.GetProperty("layers")[0].GetProperty("id").GetInt32());
        Assert.Equal("Cities", root.GetProperty("layers")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task The_root_reports_the_map_name()
    {
        var client = await MapServiceAsync();

        var root = await BodyAsync(await client.GetAsync($"{Root}/world/MapServer?f=json"));

        Assert.Equal("world", root.GetProperty("mapName").GetString());
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
    public async Task Query_accepts_the_qgis_per_feature_shape()
    {
        // QGIS fetches one feature at a time with objectIds + returnM/Z=false.
        var client = await MapServiceAsync();

        var ids = await BodyAsync(await client.GetAsync($"{Root}/world/MapServer/0/query?f=json&returnIdsOnly=true"));
        var first = ids.GetProperty("objectIds").EnumerateArray().First().GetInt64();

        var feature = await BodyAsync(await client.GetAsync(
            $"{Root}/world/MapServer/0/query?f=json&objectIds={first}&returnGeometry=true&outFields=*&returnM=false&returnZ=false"));

        Assert.NotEmpty(feature.GetProperty("features").EnumerateArray());
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
    public async Task Identify_honours_layer_defs()
    {
        var client = await MapServiceAsync();
        var point =
            $"{Root}/world/MapServer/identify?f=json" +
            "&geometry=" + Uri.EscapeDataString("{\"x\":13.405,\"y\":52.52,\"spatialReference\":{\"wkid\":4326}}") +
            "&geometryType=esriGeometryPoint&sr=4326&tolerance=5&layers=all" +
            "&mapExtent=" + Uri.EscapeDataString("-20,20,40,70") + "&imageDisplay=" + Uri.EscapeDataString("400,300,96");

        var unfiltered = await BodyAsync(await client.GetAsync(point));
        Assert.NotEmpty(unfiltered.GetProperty("results").EnumerateArray());

        var excluded = await BodyAsync(await client.GetAsync(
            point + "&layerDefs=" + Uri.EscapeDataString("{\"0\":\"population > 10000000\"}")));
        Assert.Empty(excluded.GetProperty("results").EnumerateArray());

        var included = await BodyAsync(await client.GetAsync(
            point + "&layerDefs=" + Uri.EscapeDataString("{\"0\":\"population > 1000000\"}")));
        var hits = included.GetProperty("results").EnumerateArray().ToArray();
        Assert.NotEmpty(hits);
        Assert.Equal("Berlin", hits[0].GetProperty("value").GetString());
    }

    [Fact]
    public async Task Identify_with_a_malformed_layer_defs_is_a_typed_error()
    {
        var client = await MapServiceAsync();

        var response = await client.GetAsync(
            $"{Root}/world/MapServer/identify?f=json" +
            "&geometry=" + Uri.EscapeDataString("{\"x\":13.405,\"y\":52.52,\"spatialReference\":{\"wkid\":4326}}") +
            "&geometryType=esriGeometryPoint&sr=4326&layers=all" +
            "&layerDefs=" + Uri.EscapeDataString("not-json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(400, JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Identify_accepts_time_on_a_layer_without_date_fields()
    {
        var client = await MapServiceAsync();

        var identify = await BodyAsync(await client.GetAsync(
            $"{Root}/world/MapServer/identify?f=json" +
            "&geometry=" + Uri.EscapeDataString("""{"x":13.405,"y":52.52,"spatialReference":{"wkid":4326}}""") +
            "&geometryType=esriGeometryPoint&sr=4326&tolerance=5&layers=all" +
            "&mapExtent=" + Uri.EscapeDataString("-20,20,40,70") + "&imageDisplay=" + Uri.EscapeDataString("400,300,96") +
            "&time=1199145600000"));

        var results = identify.GetProperty("results").EnumerateArray().ToArray();
        Assert.NotEmpty(results);
        Assert.Equal("Berlin", results[0].GetProperty("value").GetString());
    }

    [Fact]
    public async Task Identify_with_a_malformed_time_is_a_typed_error()
    {
        var client = await MapServiceAsync();

        var response = await client.GetAsync(
            $"{Root}/world/MapServer/identify?f=json" +
            "&geometry=" + Uri.EscapeDataString("""{"x":13.405,"y":52.52,"spatialReference":{"wkid":4326}}""") +
            "&geometryType=esriGeometryPoint&sr=4326&layers=all" +
            "&time=yesterday");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(400, JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Identify_with_an_unknown_time_relation_is_a_typed_error()
    {
        var client = await MapServiceAsync();

        var response = await client.GetAsync(
            $"{Root}/world/MapServer/identify?f=json" +
            "&geometry=" + Uri.EscapeDataString("""{"x":13.405,"y":52.52,"spatialReference":{"wkid":4326}}""") +
            "&geometryType=esriGeometryPoint&sr=4326&layers=all" +
            "&time=1199145600000&timeRelation=esriTimeRelationFoo");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("esriTimeRelationContains")]
    [InlineData("esriTimeRelationWithin")]
    public async Task Identify_with_a_time_relation_the_engine_does_not_apply_is_a_typed_error(string relation)
    {
        var client = await MapServiceAsync();

        var response = await client.GetAsync(
            $"{Root}/world/MapServer/identify?f=json" +
            "&geometry=" + Uri.EscapeDataString("""{"x":13.405,"y":52.52,"spatialReference":{"wkid":4326}}""") +
            "&geometryType=esriGeometryPoint&sr=4326&layers=all" +
            $"&time=1199145600000&timeRelation={relation}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(400, JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Identify_accepts_the_overlaps_time_relation()
    {
        var client = await MapServiceAsync();

        var identify = await BodyAsync(await client.GetAsync(
            $"{Root}/world/MapServer/identify?f=json" +
            "&geometry=" + Uri.EscapeDataString("""{"x":13.405,"y":52.52,"spatialReference":{"wkid":4326}}""") +
            "&geometryType=esriGeometryPoint&sr=4326&tolerance=5&layers=all" +
            "&mapExtent=" + Uri.EscapeDataString("-20,20,40,70") + "&imageDisplay=" + Uri.EscapeDataString("400,300,96") +
            "&time=1199145600000&timeRelation=esriTimeRelationOverlaps"));

        var results = identify.GetProperty("results").EnumerateArray().ToArray();
        Assert.NotEmpty(results);
        Assert.Equal("Berlin", results[0].GetProperty("value").GetString());
    }

    [Fact]
    public async Task Identify_accepts_layer_time_options_opting_out()
    {
        var client = await MapServiceAsync();

        var identify = await BodyAsync(await client.GetAsync(
            $"{Root}/world/MapServer/identify?f=json" +
            "&geometry=" + Uri.EscapeDataString("""{"x":13.405,"y":52.52,"spatialReference":{"wkid":4326}}""") +
            "&geometryType=esriGeometryPoint&sr=4326&tolerance=5&layers=all" +
            "&mapExtent=" + Uri.EscapeDataString("-20,20,40,70") + "&imageDisplay=" + Uri.EscapeDataString("400,300,96") +
            "&time=1199145600000&layerTimeOptions=" + Uri.EscapeDataString("""[{"id":0,"useTime":false}]""")));

        Assert.NotEmpty(identify.GetProperty("results").EnumerateArray());
    }

    [Fact]
    public async Task Identify_with_malformed_layer_time_options_is_a_typed_error()
    {
        var client = await MapServiceAsync();

        var response = await client.GetAsync(
            $"{Root}/world/MapServer/identify?f=json" +
            "&geometry=" + Uri.EscapeDataString("""{"x":13.405,"y":52.52,"spatialReference":{"wkid":4326}}""") +
            "&geometryType=esriGeometryPoint&sr=4326&layers=all" +
            "&layerTimeOptions=" + Uri.EscapeDataString("not-json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
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
    public async Task Export_without_transparent_defaults_to_an_opaque_background()
    {
        // The reference (spec §export, `transparent`: "The default is
        // false"): an export that names no transparency renders onto the
        // map background, so the workbench <img> matches the Esri panel
        // instead of showing a transparent canvas as black.
        var client = await MapServiceAsync();

        var response = await client.GetAsync(
            $"{Root}/world/MapServer/export?f=image&bbox=" + Uri.EscapeDataString("-20,20,40,70") +
            "&bboxSR=4326&imageSR=4326&size=200,150&format=png");
        var bytes = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        using var bitmap = SkiaSharp.SKBitmap.Decode(bytes);
        Assert.NotNull(bitmap);
        Assert.True(AllOpaque(bitmap),
            "The default export decoded with transparent pixels: `transparent` must default to false.");
    }

    [Fact]
    public async Task Export_with_transparent_true_keeps_a_transparent_background()
    {
        var client = await MapServiceAsync();

        var response = await client.GetAsync(
            $"{Root}/world/MapServer/export?f=image&bbox=" + Uri.EscapeDataString("-20,20,40,70") +
            "&bboxSR=4326&imageSR=4326&size=200,150&format=png&transparent=true");
        var bytes = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var bitmap = SkiaSharp.SKBitmap.Decode(bytes);
        Assert.NotNull(bitmap);
        Assert.True(HasTransparentPixel(bitmap),
            "transparent=true decoded fully opaque: the explicit transparency was lost.");
    }

    private static bool AllOpaque(SkiaSharp.SKBitmap bitmap)
    {
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).Alpha != 255)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool HasTransparentPixel(SkiaSharp.SKBitmap bitmap)
    {
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).Alpha != 255)
                {
                    return true;
                }
            }
        }

        return false;
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
    public async Task MapServer_export_contains_visible_feature_pixels_not_only_a_blank_png()
    {
        var client = await MapServiceAsync();

        // This is the same MapServer image resource used by desktop GIS
        // clients. A successful PNG response is not sufficient: decode it and
        // prove that the published city layer actually put ink on the canvas.
        var response = await client.GetAsync(
            $"{Root}/world/MapServer/export?f=image&bbox=" + Uri.EscapeDataString("-180,-90,180,90") +
            "&bboxSR=4326&imageSR=4326&size=512,256&format=png&transparent=true");
        var bytes = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        using var bitmap = SkiaSharp.SKBitmap.Decode(bytes);
        Assert.NotNull(bitmap);
        Assert.Equal(512, bitmap.Width);
        Assert.Equal(256, bitmap.Height);
        Assert.True(CountVisiblePixels(bitmap) > 0,
            "The MapServer export decoded successfully but contained no visible feature pixels.");
    }

    [Fact]
    public async Task MapServer_tile_requested_by_qgis_contains_visible_feature_pixels()
    {
        var client = await MapServiceAsync();

        // QGIS uses the ArcGIS z/y/x spelling and requests f=image. Decode
        // the returned PNG rather than trusting its status code or byte count.
        var response = await client.GetAsync($"{Root}/world/MapServer/tile/0/0/0?f=image");
        var bytes = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        using var bitmap = SkiaSharp.SKBitmap.Decode(bytes);
        Assert.NotNull(bitmap);
        Assert.Equal(256, bitmap.Width);
        Assert.Equal(256, bitmap.Height);
        Assert.True(CountVisiblePixels(bitmap) > 0,
            "The QGIS MapServer tile decoded successfully but contained no visible feature pixels.");
    }

    private static int CountVisiblePixels(SkiaSharp.SKBitmap bitmap)
    {
        var count = 0;
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).Alpha > 0)
                {
                    count++;
                }
            }
        }

        return count;
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
    public async Task A_tile_for_an_unknown_service_is_a_404_envelope()
    {
        // The tile is rendered-or-failed inside one handler, so a service that
        // was never published fails as the envelope rather than escaping it.
        var client = await MapServiceAsync();

        var response = await client.GetAsync($"{Root}/nope/MapServer/tile/0/0/0?f=image");
        var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("error");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(404, error.GetProperty("code").GetInt32());
        Assert.Equal(JsonValueKind.Array, error.GetProperty("details").ValueKind);
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

    private sealed class MapFactory(string publicationsPath) : SpatialHostFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Spatial:Admin:Token", Token);
            builder.UseSetting("Spatial:Maps:Path", publicationsPath);
        }
    }
}
