using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Spatial.Host.Tests;

/// <summary>
/// T-025: the pinned error envelope + HTTP status convention. The facade
/// returns typed HTTP statuses consistent with the engine mapping (400/404/
/// 500/503/499), not ArcGIS Server's 200-by-default, because arcgis-rest-js
/// branches on the envelope <c>error.code</c> either way while HTTP-aware
/// clients (GDAL/QGIS/service meshes) key off status. Every envelope carries
/// a <c>details</c> array.
/// </summary>
public sealed class GeoServicesErrorTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Root = "/arcgis/rest/services";

    private readonly HttpClient _client;

    public GeoServicesErrorTests(WebApplicationFactory<Program> factory) => _client = factory.CreateClient();

    private async Task<(HttpStatusCode Status, JsonElement Error)> GetErrorAsync(string path)
    {
        var response = await _client.GetAsync(path);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        return (response.StatusCode, body.GetProperty("error"));
    }

    [Fact]
    public async Task A_bad_where_clause_is_a_400_envelope()
    {
        var (status, error) = await GetErrorAsync($"{Root}/demo/FeatureServer/0/query?where=" + Uri.EscapeDataString("population = ") + "&f=json");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal(400, error.GetProperty("code").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("message").GetString()));
        Assert.Equal(JsonValueKind.Array, error.GetProperty("details").ValueKind);
    }

    [Fact]
    public async Task An_unknown_layer_is_a_404_envelope()
    {
        var (status, error) = await GetErrorAsync($"{Root}/demo/FeatureServer/999?f=json");

        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal(404, error.GetProperty("code").GetInt32());
        Assert.Equal(JsonValueKind.Array, error.GetProperty("details").ValueKind);
    }

    [Fact]
    public async Task An_unknown_service_is_a_404_envelope()
    {
        var (status, error) = await GetErrorAsync($"{Root}/nope/FeatureServer?f=json");

        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal(404, error.GetProperty("code").GetInt32());
        Assert.Equal(JsonValueKind.Array, error.GetProperty("details").ValueKind);
    }

    [Fact]
    public async Task A_bad_format_is_a_400_envelope()
    {
        var (status, error) = await GetErrorAsync($"{Root}/demo/FeatureServer/0/query?where=1%3D1&f=html");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal(400, error.GetProperty("code").GetInt32());
        Assert.Equal(JsonValueKind.Array, error.GetProperty("details").ValueKind);
    }

    [Fact]
    public async Task A_query_on_an_unknown_layer_is_a_404_envelope()
    {
        var (status, error) = await GetErrorAsync($"{Root}/demo/FeatureServer/999/query?where=1%3D1&f=json");

        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal(404, error.GetProperty("code").GetInt32());
        Assert.Equal(JsonValueKind.Array, error.GetProperty("details").ValueKind);
    }
}
