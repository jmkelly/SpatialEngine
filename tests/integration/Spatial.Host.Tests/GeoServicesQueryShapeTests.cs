using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Spatial.Host.Tests;

/// <summary>
/// The 10.x Feature Service query result shapes over the demo store
/// (ADR-0035): <c>returnExtentOnly</c> and <c>returnDistinctValues</c>,
/// including their paging and result-shape exclusivity rules, driven over
/// HTTP against the real host.
/// </summary>
public sealed class GeoServicesQueryShapeTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Cities = "/arcgis/rest/services/demo/FeatureServer/0";
    private const string Points = "/arcgis/rest/services/demo/FeatureServer/1";

    private readonly HttpClient _client;

    public GeoServicesQueryShapeTests(WebApplicationFactory<Program> factory) => _client = factory.CreateClient();

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

    [Fact]
    public async Task Return_extent_only_covers_the_full_matched_set()
    {
        var result = await GetJsonAsync($"{Cities}/query?returnExtentOnly=true&f=json");

        Assert.False(result.TryGetProperty("features", out _));
        var extent = result.GetProperty("extent");
        Assert.Equal(-3.7038, extent.GetProperty("xmin").GetDouble(), 6);
        Assert.Equal(40.4168, extent.GetProperty("ymin").GetDouble(), 6);
        Assert.Equal(16.3738, extent.GetProperty("xmax").GetDouble(), 6);
        Assert.Equal(59.9139, extent.GetProperty("ymax").GetDouble(), 6);
        Assert.Equal(4326, extent.GetProperty("spatialReference").GetProperty("wkid").GetInt32());
    }

    [Fact]
    public async Task Return_extent_only_projects_into_out_sr()
    {
        var result = await GetJsonAsync($"{Cities}/query?returnExtentOnly=true&outSR=3857&f=json");

        var extent = result.GetProperty("extent");
        Assert.Equal(3857, extent.GetProperty("spatialReference").GetProperty("wkid").GetInt32());
        Assert.True(extent.GetProperty("xmin").GetDouble() < -400_000);
        Assert.True(extent.GetProperty("xmax").GetDouble() > 1_000_000);
    }

    [Fact]
    public async Task Return_extent_only_is_null_without_matches()
    {
        var result = await GetJsonAsync(
            $"{Cities}/query?returnExtentOnly=true&where=" + Uri.EscapeDataString("name = 'Nowhere'") + "&f=json");

        Assert.Equal(JsonValueKind.Null, result.GetProperty("extent").ValueKind);
        Assert.False(result.TryGetProperty("features", out _));
    }

    [Fact]
    public async Task Return_extent_only_respects_the_where_clause()
    {
        var result = await GetJsonAsync(
            $"{Cities}/query?returnExtentOnly=true&where=" + Uri.EscapeDataString("population >= 3000000") + "&f=json");

        var extent = result.GetProperty("extent");
        Assert.Equal(-3.7038, extent.GetProperty("xmin").GetDouble(), 6);
        Assert.Equal(40.4168, extent.GetProperty("ymin").GetDouble(), 6);
        Assert.Equal(13.4050, extent.GetProperty("xmax").GetDouble(), 6);
        Assert.Equal(52.5200, extent.GetProperty("ymax").GetDouble(), 6);
    }

    [Fact]
    public async Task Return_extent_only_respects_the_geometry_filter()
    {
        var result = await GetJsonAsync(
            $"{Cities}/query?returnExtentOnly=true&geometry=" +
            Uri.EscapeDataString("""{"xmin":12,"ymin":50,"xmax":14,"ymax":54,"spatialReference":{"wkid":4326}}""") + "&f=json");

        var extent = result.GetProperty("extent");
        Assert.Equal(13.4050, extent.GetProperty("xmin").GetDouble(), 6);
        Assert.Equal(52.5200, extent.GetProperty("ymin").GetDouble(), 6);
        Assert.Equal(13.4050, extent.GetProperty("xmax").GetDouble(), 6);
        Assert.Equal(52.5200, extent.GetProperty("ymax").GetDouble(), 6);
    }

    [Fact]
    public async Task Return_distinct_values_deduplicates_the_projection()
    {
        var result = await GetJsonAsync($"{Points}/query?returnDistinctValues=true&outFields=value&f=json");

        Assert.Equal("OBJECTID", result.GetProperty("objectIdFieldName").GetString());
        Assert.True(result.TryGetProperty("fields", out _));
        var features = result.GetProperty("features").EnumerateArray().ToArray();
        Assert.Equal(20, features.Length);
        foreach (var feature in features)
        {
            Assert.False(feature.TryGetProperty("geometry", out _));
            var attributes = feature.GetProperty("attributes");
            Assert.True(attributes.TryGetProperty("value", out _));
            Assert.False(attributes.TryGetProperty("name", out _));
            Assert.False(attributes.TryGetProperty("OBJECTID", out _));
        }
    }

    [Fact]
    public async Task Return_distinct_values_defaults_to_all_non_geometry_fields()
    {
        var result = await GetJsonAsync($"{Cities}/query?returnDistinctValues=true&f=json");

        var features = result.GetProperty("features").EnumerateArray().ToArray();
        Assert.Equal(8, features.Length);
        var attributes = features[0].GetProperty("attributes");
        Assert.True(attributes.TryGetProperty("name", out _));
        Assert.True(attributes.TryGetProperty("population", out _));
        Assert.False(attributes.TryGetProperty("geometry", out _));
    }

    [Fact]
    public async Task Return_distinct_values_applies_paging_after_dedupe()
    {
        var result = await GetJsonAsync(
            $"{Points}/query?returnDistinctValues=true&outFields=value&resultOffset=5&resultRecordCount=3&f=json");

        Assert.Equal(3, result.GetProperty("features").GetArrayLength());
        Assert.True(result.GetProperty("exceededTransferLimit").GetBoolean());
    }

    [Fact]
    public async Task Return_distinct_values_respects_the_where_clause()
    {
        var result = await GetJsonAsync(
            $"{Points}/query?returnDistinctValues=true&outFields=value&where=" + Uri.EscapeDataString("value > 5") + "&f=json");

        Assert.Equal(5, result.GetProperty("features").GetArrayLength());
    }

    [Fact]
    public async Task Return_distinct_values_rejects_an_unknown_out_field()
    {
        var error = await GetErrorAsync($"{Points}/query?returnDistinctValues=true&outFields=bogus&f=json");

        Assert.Equal(400, error.GetProperty("code").GetInt32());
    }

    [Theory]
    [InlineData("returnExtentOnly=true&returnCountOnly=true")]
    [InlineData("returnExtentOnly=true&returnIdsOnly=true")]
    [InlineData("returnDistinctValues=true&returnCountOnly=true")]
    [InlineData("returnDistinctValues=true&returnIdsOnly=true")]
    [InlineData("returnExtentOnly=true&returnDistinctValues=true")]
    [InlineData("returnIdsOnly=true&returnCountOnly=true")]
    public async Task Conflicting_result_shapes_are_rejected(string parameters)
    {
        var error = await GetErrorAsync($"{Cities}/query?{parameters}&f=json");

        Assert.Equal(400, error.GetProperty("code").GetInt32());
    }
}
