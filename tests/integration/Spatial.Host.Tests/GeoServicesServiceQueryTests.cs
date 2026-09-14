using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Spatial.Host.Tests;

/// <summary>
/// T-038 item 1: the service-level <c>FeatureServer/query</c> (S1
/// query-feature-service/) over the demo store. Red-first: only
/// layer-level <c>.../&lt;id&gt;/query</c> is served today, so the service
/// route 404s while it is unmounted.
/// </summary>
public sealed class GeoServicesServiceQueryTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Service = "/arcgis/rest/services/demo/FeatureServer";

    private readonly HttpClient _client;

    public GeoServicesServiceQueryTests(WebApplicationFactory<Program> factory) => _client = factory.CreateClient();

    private async Task<JsonElement> GetJsonAsync(string path)
    {
        var response = await _client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    private async Task<JsonElement> GetErrorAsync(string path, HttpStatusCode status)
    {
        var response = await _client.GetAsync(path);
        Assert.Equal(status, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("error");
    }

    [Fact]
    public async Task The_service_query_returns_one_feature_set_per_layer()
    {
        var body = await GetJsonAsync($"{Service}/query?where=1%3D1&f=json");

        var layers = body.GetProperty("layers").EnumerateArray().ToArray();
        Assert.Equal(3, layers.Length);
        Assert.Equal(0, layers[0].GetProperty("id").GetInt32());
        Assert.Equal("OBJECTID", layers[0].GetProperty("objectIdFieldName").GetString());
        Assert.Equal(8, layers[0].GetProperty("features").EnumerateArray().Count());
        Assert.Equal(1, layers[1].GetProperty("id").GetInt32());
        Assert.Equal(2, layers[2].GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task The_service_query_count_shape_counts_each_layer()
    {
        var body = await GetJsonAsync($"{Service}/query?where=1%3D1&returnCountOnly=true&f=json");

        var layers = body.GetProperty("layers").EnumerateArray().ToArray();
        Assert.Equal(3, layers.Length);
        Assert.Equal(8, layers[0].GetProperty("count").GetInt32());
        Assert.Equal(110, layers[1].GetProperty("count").GetInt32());
        Assert.True(layers[2].GetProperty("count").GetInt32() > 30000);
    }

    [Fact]
    public async Task The_service_query_ids_shape_lists_ids_per_layer()
    {
        var body = await GetJsonAsync($"{Service}/query?where=name+%3D+%27point-042%27&returnIdsOnly=true&f=json");

        var layers = body.GetProperty("layers").EnumerateArray().ToArray();
        Assert.Equal(3, layers.Length);
        Assert.Empty(layers[0].GetProperty("objectIds").EnumerateArray());
        Assert.Equal([43L], layers[1].GetProperty("objectIds").EnumerateArray().Select(id => id.GetInt64()).ToArray());
        Assert.Empty(layers[2].GetProperty("objectIds").EnumerateArray());
    }

    [Fact]
    public async Task Layer_defs_narrow_individual_layers()
    {
        var defs = Uri.EscapeDataString("""{"0": "population > 8000000"}""");
        var body = await GetJsonAsync($"{Service}/query?where=1%3D1&layerDefs={defs}&f=json");

        var layers = body.GetProperty("layers").EnumerateArray().ToArray();
        var layer = Assert.Single(layers);
        Assert.Equal(0, layer.GetProperty("id").GetInt32());
        Assert.Single(layer.GetProperty("features").EnumerateArray());
    }

    [Fact]
    public async Task An_unknown_layer_defs_id_is_not_found()
    {
        var defs = Uri.EscapeDataString("""{"99": "1=1"}""");
        var error = await GetErrorAsync($"{Service}/query?layerDefs={defs}&f=json", HttpStatusCode.NotFound);

        Assert.Equal(404, error.GetProperty("code").GetInt32());
        Assert.Contains("99", error.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_layer_only_result_shape_names_the_layer_query()
    {
        var error = await GetErrorAsync(
            $"{Service}/query?returnExtentOnly=true&f=json", HttpStatusCode.BadRequest);

        Assert.Equal(400, error.GetProperty("code").GetInt32());
        Assert.Contains("returnExtentOnly", error.GetProperty("message").GetString(), StringComparison.Ordinal);
    }
}
