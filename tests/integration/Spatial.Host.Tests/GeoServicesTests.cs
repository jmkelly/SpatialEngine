using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Spatial.Client;
using Spatial.Esri.Codec;

namespace Spatial.Host.Tests;

/// <summary>
/// The GeoServices REST facade over the demo store (ADR-0035): catalog,
/// Geometry Service operations and the read-only Feature Service query,
/// driven over HTTP against the real host.
/// </summary>
public sealed class GeoServicesTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Root = "/arcgis/rest/services";

    private readonly HttpClient _client;

    public GeoServicesTests(WebApplicationFactory<Program> factory) => _client = factory.CreateClient();

    [Fact]
    public async Task The_feature_server_root_lists_layers_and_tables()
    {
        // The demo store is all-spatial, so tables is empty; the shape is
        // what getAllLayersAndTables reads, pinned against regressions.
        var root = await GetJsonAsync($"{Root}/demo/FeatureServer?f=json");

        Assert.NotEmpty(root.GetProperty("layers").EnumerateArray());
        Assert.Equal(JsonValueKind.Array, root.GetProperty("tables").ValueKind);
    }

    private async Task<JsonElement> GetJsonAsync(string path)
    {
        var response = await _client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    private async Task<JsonElement> PostFormAsync(string path, params (string Key, string Value)[] values)
    {
        var response = await _client.PostAsync(path, new FormUrlEncodedContent(values.Select(value => new KeyValuePair<string, string>(value.Key, value.Value))));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    [Fact]
    public async Task The_catalog_advertises_the_geometry_and_configured_feature_services()
    {
        var catalog = await GetJsonAsync($"{Root}?f=json");

        var services = catalog.GetProperty("services").EnumerateArray()
            .Select(service => (service.GetProperty("name").GetString(), service.GetProperty("type").GetString()))
            .ToArray();
        Assert.Contains(("Geometry", "GeometryServer"), services);
        Assert.Contains(("demo", "FeatureServer"), services);
    }

    [Fact]
    public async Task The_geometry_server_resource_is_served()
    {
        var info = await GetJsonAsync($"{Root}/Geometry/GeometryServer?f=json");

        Assert.True(info.GetProperty("currentVersion").GetDouble() >= 10.0);
        Assert.Contains("Project", info.GetProperty("capabilities").GetString());
    }

    [Fact]
    public async Task Project_transforms_the_geometry_array()
    {
        var geometries = await GetJsonAsync(
            $"{Root}/Geometry/GeometryServer/project?geometries=" +
            Uri.EscapeDataString("""[{"x":13.405,"y":52.52,"spatialReference":{"wkid":4326}}]""") +
            "&inSR=" + Uri.EscapeDataString("""{"wkid":4326}""") +
            "&outSR=" + Uri.EscapeDataString("""{"wkid":32632}""") + "&f=json");

        var first = geometries.GetProperty("geometries")[0];
        Assert.Equal(32632, first.GetProperty("spatialReference").GetProperty("wkid").GetInt32());
        Assert.True(first.GetProperty("x").GetDouble() > 100000);
    }

    [Fact]
    public async Task Generalize_simplifies_a_polyline()
    {
        var geometries = await GetJsonAsync(
            $"{Root}/Geometry/GeometryServer/generalize?geometries=" +
            Uri.EscapeDataString("""[{"paths":[[[0,0],[1,0.1],[2,-0.1],[3,5],[4,6],[5,7],[6,8.1],[7,9],[8,9]]]}]""") +
            "&maxDeviation=1&f=json");

        var path = geometries.GetProperty("geometries")[0].GetProperty("paths")[0];
        Assert.True(path.GetArrayLength() < 9);
    }

    [Fact]
    public async Task Generalize_and_simplify_are_distinct_operations()
    {
        // A self-intersecting bow-tie: generalize (Douglas-Peucker) leaves the
        // single invalid ring; simplify (repair) splits it into valid parts.
        const string bowTie = """{"rings":[[[0,0],[2,2],[2,0],[0,2],[0,0]]]}""";

        var generalized = await GetJsonAsync(
            $"{Root}/Geometry/GeometryServer/generalize?geometries=" + Uri.EscapeDataString($"[{bowTie}]") + "&maxDeviation=0&f=json");
        var repaired = await GetJsonAsync(
            $"{Root}/Geometry/GeometryServer/simplify?geometries=" + Uri.EscapeDataString($"[{bowTie}]") + "&f=json");

        var client = new SpatialClient(_client);
        var original = EsriGeometryCodec.Decode(JsonDocument.Parse(bowTie).RootElement);
        var repairedGeometry = EsriGeometryCodec.Decode(repaired.GetProperty("geometries")[0]);

        Assert.False(await client.ValidateAsync(original));
        Assert.True(await client.ValidateAsync(repairedGeometry));
        Assert.Equal(1, generalized.GetProperty("geometries")[0].GetProperty("rings").GetArrayLength());
        Assert.Equal(2, repaired.GetProperty("geometries")[0].GetProperty("rings").GetArrayLength());
    }

    [Fact]
    public async Task Buffer_wraps_a_point()
    {
        var geometries = await GetJsonAsync(
            $"{Root}/Geometry/GeometryServer/buffer?geometries=" +
            Uri.EscapeDataString("""[{"x":0,"y":0,"spatialReference":{"wkid":4326}}]""") + "&distances=1&f=json");

        var ring = geometries.GetProperty("geometries")[0].GetProperty("rings")[0];
        var xs = ring.EnumerateArray().Select(point => point[0].GetDouble()).ToArray();
        Assert.True(xs.Min() < -0.9 && xs.Max() > 0.9);
    }

    [Fact]
    public async Task A_buffer_with_units_buffers_in_the_projected_crs()
    {
        // distances=1000&unit=9001 (metres) against a 4326 point buffered in
        // 3857: a ~1000 m planar buffer returned in 4326.
        var geometries = await GetJsonAsync(
            $"{Root}/Geometry/GeometryServer/buffer?geometries=" +
            Uri.EscapeDataString("""[{"x":0,"y":0,"spatialReference":{"wkid":4326}}]""") +
            "&inSR=4326&bufferSR=3857&outSR=4326&distances=1000&unit=9001&f=json");

        var ring = geometries.GetProperty("geometries")[0].GetProperty("rings")[0];
        var xs = ring.EnumerateArray().Select(point => point[0].GetDouble()).ToArray();
        var width = xs.Max() - xs.Min();
        Assert.True(width > 0.015 && width < 0.022, $"Unexpected buffer width {width} degrees for 1000 m at the equator.");
        Assert.Equal(4326, geometries.GetProperty("geometries")[0].GetProperty("spatialReference").GetProperty("wkid").GetInt32());
    }

    [Fact]
    public async Task A_buffer_with_an_unknown_unit_is_a_typed_failure()
    {
        var response = await _client.GetAsync(
            $"{Root}/Geometry/GeometryServer/buffer?geometries=" + Uri.EscapeDataString("""[{"x":0,"y":0}]""") + "&distances=1&unit=424242&f=json");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(400, (await ErrorAsync(response)).GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Find_transformations_lists_the_catalogue_path()
    {
        var same = await GetJsonAsync($"{Root}/Geometry/GeometryServer/findTransformations?inSR=4326&outSR=3857&f=json");
        Assert.Equal(0, same.GetArrayLength());

        var stepped = await GetJsonAsync($"{Root}/Geometry/GeometryServer/findTransformations?inSR=4326&outSR=27700&f=json");
        Assert.Equal(1, stepped.GetArrayLength());
        Assert.True(stepped[0].GetProperty("geoTransforms")[0].GetProperty("transformForward").GetBoolean());
    }

    [Theory]
    [InlineData("fromGeoCoordinateString")]
    [InlineData("toGeoCoordinateString")]
    public async Task Coordinate_notation_operations_are_typed_failures(string operation)
    {
        var response = await _client.GetAsync(
            $"{Root}/Geometry/GeometryServer/{operation}?conversionType=MGRS&f=json");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(400, (await ErrorAsync(response)).GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Intersect_returns_one_geometry_per_input()
    {
        var geometries = await GetJsonAsync(
            $"{Root}/Geometry/GeometryServer/intersect?geometries=" +
            Uri.EscapeDataString("""[{"rings":[[[0,0],[0,2],[2,2],[2,0],[0,0]]]}]""") +
            "&geometry=" + Uri.EscapeDataString("""{"xmin":1,"ymin":1,"xmax":3,"ymax":3}""") + "&f=json");

        var first = geometries.GetProperty("geometries")[0];
        var points = first.GetProperty("rings")[0].EnumerateArray().ToArray();
        var xs = points.Select(point => point[0].GetDouble()).ToArray();
        var ys = points.Select(point => point[1].GetDouble()).ToArray();
        Assert.Equal(1.0, xs.Min());
        Assert.Equal(2.0, xs.Max());
        Assert.Equal(1.0, ys.Min());
        Assert.Equal(2.0, ys.Max());
    }

    [Fact]
    public async Task An_unsupported_format_is_a_typed_error()
    {
        var response = await _client.GetAsync($"{Root}?f=html");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await ErrorAsync(response);
        Assert.Equal(400, error.GetProperty("code").GetInt32());
        Assert.Contains("f=json", error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task The_url_geometry_form_is_rejected()
    {
        var response = await _client.GetAsync(
            $"{Root}/Geometry/GeometryServer/buffer?geometries=" +
            Uri.EscapeDataString("""[{"url":"https://example.com/x.json"}]""") + "&distances=1&f=json");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(400, (await ErrorAsync(response)).GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task The_feature_server_lists_the_demo_layers()
    {
        var root = await GetJsonAsync($"{Root}/demo/FeatureServer?f=json");

        var layers = root.GetProperty("layers").EnumerateArray().Select(layer => layer.GetProperty("name").GetString()).ToArray();
        Assert.Equal(3, layers.Length);
        Assert.Equal("cities", layers[0]);
        Assert.Equal("world_cities", layers[2]);
    }

    [Fact]
    public async Task The_layer_resource_describes_the_schema()
    {
        var layer = await GetJsonAsync($"{Root}/demo/FeatureServer/0?f=json");

        Assert.Equal("OBJECTID", layer.GetProperty("objectIdField").GetString());
        Assert.Equal("esriGeometryPoint", layer.GetProperty("geometryType").GetString());
        var fields = layer.GetProperty("fields").EnumerateArray().Select(field => field.GetProperty("name").GetString()).ToArray();
        Assert.Contains("name", fields);
        Assert.Contains("population", fields);
    }

    [Fact]
    public async Task A_query_filters_by_where_clause()
    {
        var result = await GetJsonAsync($"{Root}/demo/FeatureServer/0/query?where=" + Uri.EscapeDataString("name = 'Berlin'") + "&f=json");

        var features = result.GetProperty("features").EnumerateArray().ToArray();
        Assert.Single(features);
        Assert.Equal("Berlin", features[0].GetProperty("attributes").GetProperty("name").GetString());
    }

    [Fact]
    public async Task A_query_supports_like_and_null_tests()
    {
        var result = await GetJsonAsync($"{Root}/demo/FeatureServer/0/query?where=" + Uri.EscapeDataString("name LIKE 'B%' OR name IS NULL") + "&f=json");

        Assert.Single(result.GetProperty("features").EnumerateArray());
    }

    [Fact]
    public async Task A_query_filters_by_the_synthetic_object_id()
    {
        // OBJECTID is the layer's advertised object-id field, so it must be
        // referenceable in where even though the adapter synthesises it.
        var result = await GetJsonAsync(
            $"{Root}/demo/FeatureServer/0/query?where=" + Uri.EscapeDataString("OBJECTID = 3") + "&outFields=name&f=json");

        var feature = Assert.Single(result.GetProperty("features").EnumerateArray());
        Assert.Equal(3, feature.GetProperty("attributes").GetProperty("OBJECTID").GetInt64());
        Assert.Equal("London", feature.GetProperty("attributes").GetProperty("name").GetString());
    }

    [Fact]
    public async Task A_synthetic_object_id_filter_composes_with_order_and_paging()
    {
        var result = await GetJsonAsync(
            $"{Root}/demo/FeatureServer/0/query?where=" + Uri.EscapeDataString("OBJECTID > 5")
            + "&outFields=name&orderByFields=OBJECTID&f=json");

        Assert.Equal(
            [6, 7, 8],
            result.GetProperty("features").EnumerateArray()
                .Select(feature => feature.GetProperty("attributes").GetProperty("OBJECTID").GetInt64())
                .ToArray());
    }

    [Fact]
    public async Task A_synthetic_object_id_filter_matches_nothing_for_an_absent_id()
    {
        var result = await GetJsonAsync(
            $"{Root}/demo/FeatureServer/0/query?where=" + Uri.EscapeDataString("OBJECTID = 999") + "&f=json");

        Assert.Empty(result.GetProperty("features").EnumerateArray());
    }

    [Fact]
    public async Task A_query_filters_by_an_envelope_geometry()
    {
        var result = await GetJsonAsync(
            $"{Root}/demo/FeatureServer/0/query?geometry=" + Uri.EscapeDataString("""{"xmin":12,"ymin":50,"xmax":14,"ymax":54,"spatialReference":{"wkid":4326}}""") + "&f=json");

        Assert.Single(result.GetProperty("features").EnumerateArray());
    }

    [Fact]
    public async Task Return_ids_only_projects_the_object_ids()
    {
        var result = await GetJsonAsync($"{Root}/demo/FeatureServer/0/query?returnIdsOnly=true&f=json");

        Assert.Equal("OBJECTID", result.GetProperty("objectIdFieldName").GetString());
        Assert.Equal(8, result.GetProperty("objectIds").GetArrayLength());
    }

    [Fact]
    public async Task Return_count_only_reports_the_total()
    {
        var result = await GetJsonAsync($"{Root}/demo/FeatureServer/0/query?returnCountOnly=true&f=json");

        Assert.Equal(8, result.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Out_fields_projects_the_attributes()
    {
        var result = await GetJsonAsync($"{Root}/demo/FeatureServer/0/query?outFields=population&f=json");

        var attributes = result.GetProperty("features")[0].GetProperty("attributes");
        Assert.True(attributes.TryGetProperty("population", out _));
        Assert.False(attributes.TryGetProperty("name", out _));
    }

    [Fact]
    public async Task Query_projects_features_into_out_sr()
    {
        var result = await GetJsonAsync(
            $"{Root}/demo/FeatureServer/0/query?outSR=3857&f=json");

        var feature = result.GetProperty("features")[0];
        var geometry = feature.GetProperty("geometry");
        Assert.Equal(3857, geometry.GetProperty("spatialReference").GetProperty("wkid").GetInt32());
        Assert.True(geometry.GetProperty("x").GetDouble() > 100_000);
    }

    [Fact]
    public async Task An_unknown_service_is_a_404()
    {
        var response = await _client.GetAsync($"{Root}/missing/FeatureServer?f=json");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(404, (await ErrorAsync(response)).GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task An_unknown_layer_is_a_404()
    {
        var response = await _client.GetAsync($"{Root}/demo/FeatureServer/99?f=json");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task An_unsupported_where_construct_is_rejected()
    {
        var response = await _client.GetAsync($"{Root}/demo/FeatureServer/0/query?where=" + Uri.EscapeDataString("1=1; DROP TABLE") + "&f=json");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_resource_read_is_accepted_as_a_form_post()
    {
        // The spec allows GET or POST for resources; ArcGIS REST JS (and so
        // the Maps SDK) POSTs reads. Without this the client sees a 405.
        var catalog = await PostFormAsync(Root, ("f", "json"));
        Assert.NotEmpty(catalog.GetProperty("services").EnumerateArray());

        var info = await PostFormAsync($"{Root}/Geometry/GeometryServer", ("f", "json"));
        Assert.Contains("Project", info.GetProperty("capabilities").GetString());

        var root = await PostFormAsync($"{Root}/demo/FeatureServer", ("f", "json"));
        Assert.NotEmpty(root.GetProperty("layers").EnumerateArray());

        var layer = await PostFormAsync($"{Root}/demo/FeatureServer/0", ("f", "json"));
        Assert.Equal("OBJECTID", layer.GetProperty("objectIdField").GetString());
    }

    [Fact]
    public async Task The_feature_resource_reads_one_feature_by_object_id()
    {
        var feature = await GetJsonAsync($"{Root}/demo/FeatureServer/0/1?f=json");

        var attributes = feature.GetProperty("feature").GetProperty("attributes");
        Assert.Equal(1, attributes.GetProperty("OBJECTID").GetInt64());

        var missing = await _client.GetAsync($"{Root}/demo/FeatureServer/0/9999?f=json");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(404, (await ErrorAsync(missing)).GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task The_esri_match_all_where_is_supported()
    {
        // ArcGIS REST JS defaults to where=1=1; the closed grammar must treat
        // it as a constant predicate rather than reject it as a bad field.
        var result = await GetJsonAsync($"{Root}/demo/FeatureServer/0/query?where=" + Uri.EscapeDataString("1=1") + "&f=json");

        Assert.Equal(8, result.GetProperty("features").GetArrayLength());
    }

    [Fact]
    public async Task Geometry_operations_accept_a_form_post()
    {
        var result = await PostFormAsync(
            $"{Root}/Geometry/GeometryServer/buffer",
            ("geometries", """[{"x":0,"y":0}]"""),
            ("distances", "1"),
            ("f", "json"));

        Assert.Equal(1, result.GetProperty("geometries").GetArrayLength());
    }

    private static async Task<JsonElement> ErrorAsync(HttpResponseMessage response)
    {
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        return body.GetProperty("error");
    }
}
