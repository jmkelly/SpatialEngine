using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Spatial.Host.Tests;

/// <summary>
/// T-016: <c>f=pjson</c> is accepted as a JSON alias wherever <c>f=json</c> is.
/// Red test first: replays real-client (GDAL ESRIJSON driver, pygeoapi)
/// request shapes with <c>f=pjson</c> against the serve surface.
/// </summary>
public sealed class GeoServicesFormatTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Root = "/arcgis/rest/services";

    private readonly HttpClient _client;

    public GeoServicesFormatTests(WebApplicationFactory<Program> factory) => _client = factory.CreateClient();

    [Theory]
    [InlineData("/arcgis/rest/services?f=pjson")]
    [InlineData("/arcgis/rest/services/demo/FeatureServer?f=pjson")]
    [InlineData("/arcgis/rest/services/demo/FeatureServer/0?f=pjson")]
    [InlineData("/arcgis/rest/services/demo/FeatureServer/0/query?where=1%3D1&outFields=*&f=pjson")]
    [InlineData("/arcgis/rest/services/demo/FeatureServer/0/1?f=pjson")]
    public async Task Pjson_is_accepted_wherever_json_is(string path)
    {
        var response = await _client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("application/json", response.Content.Headers.ContentType?.MediaType ?? string.Empty);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
    }

    [Fact]
    public async Task Pjson_query_returns_the_same_features_as_json()
    {
        var json = await GetJsonAsync($"{Root}/demo/FeatureServer/0/query?where=1%3D1&outFields=name&orderByFields=OBJECTID&f=json");
        var pjson = await GetJsonAsync($"{Root}/demo/FeatureServer/0/query?where=1%3D1&outFields=name&orderByFields=OBJECTID&f=pjson");
        Assert.Equal(
            Names(json),
            Names(pjson));
    }

    private static string[] Names(JsonElement result) =>
        result.GetProperty("features").EnumerateArray()
            .Select(feature => feature.GetProperty("attributes").GetProperty("name").GetString() ?? string.Empty)
            .ToArray();

    private async Task<JsonElement> GetJsonAsync(string path)
    {
        var response = await _client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }
}
