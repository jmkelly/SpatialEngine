using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Spatial.Host.Tests;

/// <summary>
/// T-022: the temporal surface over HTTP. The demo store carries no date
/// fields, so <c>time</c> is accepted and passes everything through (ArcGIS
/// Server ignores <c>time</c> on non-time-aware layers); date-typed filtering
/// itself is proved by the unit tests over date-bearing features.
/// </summary>
public sealed class GeoServicesTemporalTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Cities = "/arcgis/rest/services/demo/FeatureServer/0/query";

    private readonly HttpClient _client;

    public GeoServicesTemporalTests(WebApplicationFactory<Program> factory) => _client = factory.CreateClient();

    private async Task<JsonElement> GetJsonAsync(string path)
    {
        var response = await _client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    [Fact]
    public async Task Time_is_accepted_on_a_layer_without_date_fields()
    {
        var result = await GetJsonAsync($"{Cities}?time=1199145600000&f=json");

        Assert.True(result.GetProperty("features").GetArrayLength() > 0);
    }

    [Fact]
    public async Task A_time_extent_with_null_bounds_is_accepted()
    {
        var result = await GetJsonAsync($"{Cities}?time=null%2C1199145600000&f=json");

        Assert.True(result.GetProperty("features").GetArrayLength() > 0);
    }

    [Fact]
    public async Task A_malformed_time_is_a_typed_error()
    {
        var response = await _client.GetAsync($"{Cities}?time=yesterday&f=json");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(400, JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task A_date_literal_names_its_unknown_field()
    {
        var response = await _client.GetAsync(
            $"{Cities}?where=" + Uri.EscapeDataString("observed >= TIMESTAMP '2024-01-01 00:00:00'") + "&f=json");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("error");
        Assert.Contains("observed", error.GetProperty("message").GetString());
    }
}
