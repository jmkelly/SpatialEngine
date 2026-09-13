using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Spatial.Host.Tests;

/// <summary>
/// T-021: returnExceededLimitFeatures + maxRecordCountFactor over the 34k
/// world-cities layer. Without a factor the page caps at maxRecordCount
/// (1000); the factor multiplies the cap. The REST JS queryAllFeatures loop
/// sends returnExceededLimitFeatures=true and pages off maxRecordCount.
/// </summary>
public sealed class GeoServicesPagingTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string WorldCities = "/arcgis/rest/services/demo/FeatureServer/2";
    private readonly HttpClient _client;
    public GeoServicesPagingTests(WebApplicationFactory<Program> factory) => _client = factory.CreateClient();

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
    public async Task Oversized_pages_cap_at_max_record_count_without_a_factor()
    {
        var result = await GetJsonAsync($"{WorldCities}/query?where=1%3D1&outFields=*&returnExceededLimitFeatures=true&resultRecordCount=5000&f=json");
        Assert.Equal(1000, result.GetProperty("features").GetArrayLength());
        Assert.True(result.GetProperty("exceededTransferLimit").GetBoolean());
    }

    [Fact]
    public async Task Max_record_count_factor_multiplies_the_cap()
    {
        var result = await GetJsonAsync($"{WorldCities}/query?where=1%3D1&outFields=*&returnExceededLimitFeatures=true&resultRecordCount=2500&maxRecordCountFactor=4&f=json");
        Assert.Equal(2500, result.GetProperty("features").GetArrayLength());
        Assert.True(result.GetProperty("exceededTransferLimit").GetBoolean());
    }

    [Fact]
    public async Task A_factor_does_not_raise_pages_above_its_product()
    {
        var result = await GetJsonAsync($"{WorldCities}/query?where=1%3D1&outFields=*&resultRecordCount=5000&maxRecordCountFactor=2&f=json");
        Assert.Equal(2000, result.GetProperty("features").GetArrayLength());
        Assert.True(result.GetProperty("exceededTransferLimit").GetBoolean());
    }

    [Fact]
    public async Task An_invalid_factor_is_rejected()
    {
        var error = await GetErrorAsync($"{WorldCities}/query?where=1%3D1&maxRecordCountFactor=0&f=json");
        Assert.Equal(400, error.GetProperty("code").GetInt32());
    }

    /// <summary>
    /// T-015 closeout: replays the exact <c>queryAllFeatures</c> algorithm
    /// the real arcgis-rest-js client runs (read <c>maxRecordCount</c> off
    /// the layer, page with <c>returnExceededLimitFeatures=true</c>, stop
    /// when <c>returnedCount &lt; pageSize || !exceededTransferLimit</c>).
    /// The loop must terminate with the full matched set, in stable order.
    /// </summary>
    [Fact]
    public async Task A_query_all_features_loop_terminates_with_the_full_set()
    {
        var layer = await GetJsonAsync($"{WorldCities}?f=json");
        var pageSize = layer.GetProperty("maxRecordCount").GetInt32();
        Assert.True(pageSize > 0);

        var expected = await GetJsonAsync($"{WorldCities}/query?where=1%3D1&returnCountOnly=true&f=json");
        var total = expected.GetProperty("count").GetInt32();
        Assert.True(total > pageSize);

        var collected = new List<long>();
        var offset = 0;
        while (true)
        {
            var page = await GetJsonAsync(
                $"{WorldCities}/query?where=1%3D1&outFields=*&orderByFields=OBJECTID" +
                $"&returnExceededLimitFeatures=true&resultOffset={offset}&resultRecordCount={pageSize}&f=json");
            var features = page.GetProperty("features").EnumerateArray().ToArray();
            foreach (var feature in features)
            {
                collected.Add(feature.GetProperty("attributes").GetProperty("OBJECTID").GetInt64());
            }

            var exceeded = !page.TryGetProperty("exceededTransferLimit", out var flag) || flag.GetBoolean();
            offset += features.Length;
            if (features.Length < pageSize || !exceeded)
            {
                break;
            }
        }

        Assert.Equal(total, collected.Count);
        Assert.Equal(collected.OrderBy(id => id).ToArray(), collected.ToArray());
        Assert.Equal(collected.Count, collected.Distinct().Count());
    }
}
