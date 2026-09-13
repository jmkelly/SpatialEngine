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

    /// <summary>
    /// T-018: the layer and service root advertise truthful
    /// <c>advancedQueryCapabilities</c> + <c>supportedQueryFormats</c>.
    /// Every flag names behaviour proved by its own test: pagination and
    /// orderBy honoured, statistics/having rejected, distinct values and
    /// query extent served, non-standardized closed where-grammar.
    /// </summary>
    [Fact]
    public async Task Layer_advertises_truthful_query_capabilities()
    {
        var layer = await GetJsonAsync($"{Root}/demo/FeatureServer/0?f=json");

        Assert.Equal("JSON", layer.GetProperty("supportedQueryFormats").GetString());
        Assert.False(layer.GetProperty("supportsStatistics").GetBoolean());
        Assert.True(layer.GetProperty("supportsAdvancedQueries").GetBoolean());

        var capabilities = layer.GetProperty("advancedQueryCapabilities");
        Assert.True(capabilities.GetProperty("supportsPagination").GetBoolean());
        Assert.True(capabilities.GetProperty("supportsOrderBy").GetBoolean());
        Assert.False(capabilities.GetProperty("supportsStatistics").GetBoolean());
        Assert.True(capabilities.GetProperty("supportsDistinct").GetBoolean());
        Assert.False(capabilities.GetProperty("supportsHavingClause").GetBoolean());
        Assert.True(capabilities.GetProperty("supportsReturningQueryExtent").GetBoolean());
        Assert.False(capabilities.GetProperty("useStandardizedQueries").GetBoolean());
    }

    [Fact]
    public async Task Service_root_advertises_truthful_query_capabilities()
    {
        var root = await GetJsonAsync($"{Root}/demo/FeatureServer?f=json");

        Assert.Equal("JSON", root.GetProperty("supportedQueryFormats").GetString());
        var capabilities = root.GetProperty("advancedQueryCapabilities");
        Assert.True(capabilities.GetProperty("supportsPagination").GetBoolean());
        Assert.True(capabilities.GetProperty("supportsOrderBy").GetBoolean());
        Assert.False(capabilities.GetProperty("supportsStatistics").GetBoolean());
    }
    /// <summary>
    /// T-017: <c>f=geojson</c> on query is honestly rejected — the facade
    /// serves Esri JSON only, so the 400 names <c>supportedQueryFormats</c>
    /// and the layer advertises the truthful value (see T-018).
    /// </summary>
    [Fact]
    public async Task Geojson_on_query_is_rejected_naming_supported_query_formats()
    {
        var response = await _client.GetAsync(
            $"{Root}/demo/FeatureServer/0/query?where=1%3D1&outFields=*&f=geojson");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("error");
        Assert.Equal(400, error.GetProperty("code").GetInt32());
        Assert.Contains("supportedQueryFormats", error.GetProperty("message").GetString(), StringComparison.Ordinal);
    }
}
