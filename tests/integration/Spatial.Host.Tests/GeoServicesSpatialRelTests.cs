using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Spatial.Host.Tests;

/// <summary>
/// T-023: remaining spatialRel predicates + quantization/geometryPrecision policy.
/// Points (cities) against envelope queries: Within/Intersects find Berlin,
/// Contains (point contains envelope) is empty, Overlaps/Crosses are false
/// for point-vs-polygon, Touches needs a boundary case. Quantization
/// quantizes; geometryPrecision rounds; maxAllowableOffset is honoured.
/// </summary>
public sealed class GeoServicesSpatialRelTests : IClassFixture<PostgisHostFactory>
{
    private const string Cities = "/arcgis/rest/services/demo/FeatureServer/0";
    private readonly HttpClient _client;
    public GeoServicesSpatialRelTests(PostgisHostFactory factory) => _client = factory.CreateClient();

    private async Task<JsonElement> GetJsonAsync(string path)
    {
        var response = await _client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    private async Task<JsonElement> GetErrorAsync(string path)
    {
        var response = await _client.GetAsync(path);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("error");
    }

    private static string EnvelopeQuery(string spatialRel) =>
        $"{Cities}/query?geometry=" + Uri.EscapeDataString("""{"xmin":13,"ymin":52,"xmax":14,"ymax":53,"spatialReference":{"wkid":4326}}""")
        + $"&geometryType=esriGeometryEnvelope&spatialRel={spatialRel}&outFields=name&f=json";

    [Fact]
    public async Task Within_finds_berlin_in_a_small_envelope()
    {
        var result = await GetJsonAsync(EnvelopeQuery("esriSpatialRelWithin"));
        var names = result.GetProperty("features").EnumerateArray()
            .Select(feature => feature.GetProperty("attributes").GetProperty("name").GetString()).ToArray();
        Assert.Contains("Berlin", names);
    }

    [Fact]
    public async Task Contains_is_empty_for_points_against_an_envelope()
    {
        var result = await GetJsonAsync(EnvelopeQuery("esriSpatialRelContains"));
        Assert.Empty(result.GetProperty("features").EnumerateArray());
    }

    [Fact]
    public async Task Overlaps_and_crosses_are_false_for_point_vs_polygon()
    {
        var overlaps = await GetJsonAsync(EnvelopeQuery("esriSpatialRelOverlaps"));
        Assert.Empty(overlaps.GetProperty("features").EnumerateArray());
        var crosses = await GetJsonAsync(EnvelopeQuery("esriSpatialRelCrosses"));
        Assert.Empty(crosses.GetProperty("features").EnumerateArray());
    }

    [Fact]
    public async Task Touches_excludes_equal_points_while_intersects_finds_them()
    {
        var geometry = Uri.EscapeDataString("""{"x":13.405,"y":52.52,"spatialReference":{"wkid":4326}}""");
        var pointQuery = $"{Cities}/query?geometry={geometry}&geometryType=esriGeometryPoint&spatialRel=";
        var intersects = await GetJsonAsync(pointQuery + "esriSpatialRelIntersects&outFields=name&f=json");
        Assert.Single(intersects.GetProperty("features").EnumerateArray());
        var touches = await GetJsonAsync(pointQuery + "esriSpatialRelTouches&outFields=name&f=json");
        Assert.Empty(touches.GetProperty("features").EnumerateArray());
    }

    [Fact]
    public async Task Index_intersects_stays_rejected_with_a_named_alternative()
    {
        var error = await GetErrorAsync(EnvelopeQuery("esriSpatialRelIndexIntersects"));
        Assert.Equal(400, error.GetProperty("code").GetInt32());
        Assert.Contains("EnvelopeIntersects", error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Quantization_returns_a_quantized_geometry()
    {
        var quantization = Uri.EscapeDataString("""{"mode":"view","originPosition":"upperLeft","tolerance":1.09,"extent":{"xmin":0,"ymin":0,"xmax":10,"ymax":10}}""");
        var result = await GetJsonAsync($"{Cities}/query?where=" + Uri.EscapeDataString("name = 'Berlin'") + $"&quantizationParameters={quantization}&f=json");

        var geometry = result.GetProperty("features")[0].GetProperty("geometry");
        // Berlin is 13.405/52.52; the grid is anchored on the view extent, so
        // x snaps to the 12th step of 1.09 and y to the 39th step below 10.
        Assert.Equal(12 * 1.09, geometry.GetProperty("x").GetDouble(), 6);
        Assert.Equal(10 + (39 * 1.09), geometry.GetProperty("y").GetDouble(), 6);
    }

    [Fact]
    public async Task Geometry_precision_rounds_coordinates()
    {
        var result = await GetJsonAsync($"{Cities}/query?where=" + Uri.EscapeDataString("name = 'Berlin'") + "&geometryPrecision=1&f=json");
        var geometry = result.GetProperty("features")[0].GetProperty("geometry");
        Assert.Equal(13.4, geometry.GetProperty("x").GetDouble(), 9);
        Assert.Equal(52.5, geometry.GetProperty("y").GetDouble(), 9);
    }

    [Fact]
    public async Task Max_allowable_offset_is_honoured_and_zero_is_full_precision()
    {
        var full = await GetJsonAsync($"{Cities}/query?where=" + Uri.EscapeDataString("name = 'Berlin'") + "&f=json");
        var zero = await GetJsonAsync($"{Cities}/query?where=" + Uri.EscapeDataString("name = 'Berlin'") + "&maxAllowableOffset=0&f=json");
        var wide = await GetJsonAsync($"{Cities}/query?where=" + Uri.EscapeDataString("name = 'Berlin'") + "&maxAllowableOffset=1000&f=json");

        // The demo layer's city points are single vertices, so the allowance
        // has nothing to spend: the response is the stored point either way,
        // which is what "deviate by at most n" promises. The polygon path is
        // pinned by the adapter unit tests.
        Assert.Equal(
            full.GetProperty("features")[0].GetProperty("geometry").GetProperty("x").GetDouble(),
            zero.GetProperty("features")[0].GetProperty("geometry").GetProperty("x").GetDouble());
        Assert.Single(wide.GetProperty("features").EnumerateArray());
    }
}
