using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Spatial.Host.Tests;

/// <summary>
/// T-020: inSR is honoured for query geometry. The same 3857 envelope is
/// empty when misread as 4326 but finds Berlin once declared as 3857.
/// </summary>
public sealed class GeoServicesInSrTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Cities = "/arcgis/rest/services/demo/FeatureServer/0";
    private readonly HttpClient _client;
    public GeoServicesInSrTests(WebApplicationFactory<Program> factory) => _client = factory.CreateClient();

    private async Task<JsonElement> GetJsonAsync(string path)
    {
        var response = await _client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    [Fact]
    public async Task A_3857_envelope_needs_its_in_sr_to_match()
    {
        const string envelope3857 = """{"xmin":1000000,"ymin":6000000,"xmax":2000000,"ymax":7000000}""";
        var geometry = Uri.EscapeDataString(envelope3857);

        var misread = await GetJsonAsync($"{Cities}/query?geometry={geometry}&f=json");
        Assert.Empty(misread.GetProperty("features").EnumerateArray());

        var honoured = await GetJsonAsync($"{Cities}/query?geometry={geometry}&inSR=3857&f=json");
        var names = honoured.GetProperty("features").EnumerateArray()
            .Select(feature => feature.GetProperty("attributes").GetProperty("name").GetString()).ToArray();
        Assert.Contains("Berlin", names);
    }
}
