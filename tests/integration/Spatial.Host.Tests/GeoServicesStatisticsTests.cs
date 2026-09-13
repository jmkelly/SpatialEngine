using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Spatial.Host.Tests;

/// <summary>
/// T-019: outStatistics/groupBy/having over the demo cities (spec §9.1.4,
/// 10.x statistics). Shape per Koop render-statistics: no geometry,
/// attributes only.
/// </summary>
public sealed class GeoServicesStatisticsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Cities = "/arcgis/rest/services/demo/FeatureServer/0";
    private readonly HttpClient _client;
    public GeoServicesStatisticsTests(WebApplicationFactory<Program> factory) => _client = factory.CreateClient();

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
    public async Task Sum_population_over_all_cities()
    {
        var stats = Uri.EscapeDataString("""[{"statisticType":"sum","onStatisticField":"population","outStatisticFieldName":"sumpop"}]""");
        var result = await GetJsonAsync($"{Cities}/query?outStatistics={stats}&f=json");
        var row = Assert.Single(result.GetProperty("features").EnumerateArray());
        Assert.Equal(24384000L, row.GetProperty("attributes").GetProperty("sumpop").GetInt64());
        Assert.False(row.TryGetProperty("geometry", out _));
    }

    [Fact]
    public async Task Count_min_max_avg_population()
    {
        var stats = Uri.EscapeDataString("""[{"statisticType":"count","onStatisticField":"*","outStatisticFieldName":"n"},{"statisticType":"min","onStatisticField":"population","outStatisticFieldName":"lo"},{"statisticType":"max","onStatisticField":"population","outStatisticFieldName":"hi"},{"statisticType":"avg","onStatisticField":"population","outStatisticFieldName":"mean"}]""");
        var result = await GetJsonAsync($"{Cities}/query?outStatistics={stats}&f=json");
        var attributes = Assert.Single(result.GetProperty("features").EnumerateArray()).GetProperty("attributes");
        Assert.Equal(8, attributes.GetProperty("n").GetInt64());
        Assert.Equal(700000L, attributes.GetProperty("lo").GetInt64());
        Assert.Equal(8900000L, attributes.GetProperty("hi").GetInt64());
        Assert.Equal(3048000.0, attributes.GetProperty("mean").GetDouble(), 0.5);
    }

    [Fact]
    public async Task Group_by_name_with_having()
    {
        var stats = Uri.EscapeDataString("""[{"statisticType":"sum","onStatisticField":"population","outStatisticFieldName":"sumpop"}]""");
        var having = Uri.EscapeDataString("sumpop > 3000000");
        var result = await GetJsonAsync($"{Cities}/query?outStatistics={stats}&groupByFieldsForStatistics=name&having={having}&orderByFields=name&f=json");
        var names = result.GetProperty("features").EnumerateArray()
            .Select(feature => feature.GetProperty("attributes").GetProperty("name").GetString()).ToArray();
        Assert.True(names.SequenceEqual(["Berlin", "London", "Madrid"]));
    }

    [Fact]
    public async Task Statistics_on_empty_set_yield_one_null_row()
    {
        var stats = Uri.EscapeDataString("""[{"statisticType":"sum","onStatisticField":"population","outStatisticFieldName":"sumpop"}]""");
        var where = Uri.EscapeDataString("name = 'Nowhere'");
        var result = await GetJsonAsync($"{Cities}/query?outStatistics={stats}&where={where}&f=json");
        var row = Assert.Single(result.GetProperty("features").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("attributes").GetProperty("sumpop").ValueKind);
    }

    [Fact]
    public async Task Statistics_reject_unknown_fields_and_combine_shapes()
    {
        var badField = Uri.EscapeDataString("""[{"statisticType":"sum","onStatisticField":"bogus","outStatisticFieldName":"x"}]""");
        var error = await GetErrorAsync($"{Cities}/query?outStatistics={badField}&f=json");
        Assert.Equal(400, error.GetProperty("code").GetInt32());

        var stats = Uri.EscapeDataString("""[{"statisticType":"count","onStatisticField":"*","outStatisticFieldName":"n"}]""");
        var clash = await GetErrorAsync($"{Cities}/query?outStatistics={stats}&returnCountOnly=true&f=json");
        Assert.Equal(400, clash.GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Layer_advertises_statistics_support()
    {
        var layer = await GetJsonAsync($"{Cities}?f=json");
        Assert.True(layer.GetProperty("supportsStatistics").GetBoolean());
        Assert.True(layer.GetProperty("advancedQueryCapabilities").GetProperty("supportsStatistics").GetBoolean());
        Assert.True(layer.GetProperty("advancedQueryCapabilities").GetProperty("supportsHavingClause").GetBoolean());
    }
}
