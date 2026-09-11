using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Spatial.Host.Tests;

/// <summary>
/// The Feature Service <c>orderByFields</c> delta over the demo store: the
/// adapter validates the requested fields against the layer schema and orders
/// the matched features in memory before pagination (never as SQL).
/// </summary>
public sealed class GeoServicesOrderByTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Cities = "/arcgis/rest/services/demo/FeatureServer/0/query";

    /// <summary>Layer 1 is the 11×10 grid; its <c>value</c> is <c>x + y</c>, so ties exist.</summary>
    private const string Points = "/arcgis/rest/services/demo/FeatureServer/1/query";

    private readonly HttpClient _client;

    public GeoServicesOrderByTests(WebApplicationFactory<Program> factory) => _client = factory.CreateClient();

    [Fact]
    public async Task Order_by_population_defaults_to_ascending()
    {
        var result = await GetJsonAsync($"{Cities}?orderByFields=population&f=json");

        Assert.Equal(
            ["Oslo", "Amsterdam", "Vienna", "Paris", "Rome", "Madrid", "Berlin", "London"],
            Names(result));
    }

    [Fact]
    public async Task Order_by_population_descending_reverses_the_sequence()
    {
        var result = await GetJsonAsync($"{Cities}?orderByFields=" + Uri.EscapeDataString("population DESC") + "&f=json");

        Assert.Equal(
            ["London", "Berlin", "Madrid", "Rome", "Paris", "Vienna", "Amsterdam", "Oslo"],
            Names(result));
    }

    [Fact]
    public async Task Order_by_composes_with_offset_and_record_count()
    {
        var result = await GetJsonAsync(
            $"{Cities}?orderByFields=" + Uri.EscapeDataString("population DESC") + "&resultOffset=1&resultRecordCount=2&f=json");

        Assert.Equal(["Berlin", "Madrid"], Names(result));
    }

    [Fact]
    public async Task Order_by_breaks_ties_with_a_secondary_key()
    {
        var result = await GetJsonAsync($"{Points}?outFields=name,value&orderByFields=" + Uri.EscapeDataString("value DESC, name ASC") + "&f=json");

        var rows = result.GetProperty("features").EnumerateArray()
            .Select(feature => (
                Name: feature.GetProperty("attributes").GetProperty("name").GetString()!,
                Value: feature.GetProperty("attributes").GetProperty("value").GetDouble()))
            .ToArray();

        Assert.Equal(110, rows.Length);
        for (var i = 1; i < rows.Length; i++)
        {
            Assert.True(rows[i - 1].Value >= rows[i].Value, "the primary key must be non-increasing");
            if (rows[i - 1].Value == rows[i].Value)
            {
                Assert.True(string.CompareOrdinal(rows[i - 1].Name, rows[i].Name) < 0, "ties must break by name ascending");
            }
        }
    }

    [Fact]
    public async Task Order_by_an_unknown_field_is_a_400()
    {
        var response = await _client.GetAsync($"{Cities}?orderByFields=bogus&f=json");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(400, (await ErrorAsync(response)).GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Order_by_a_geometry_field_is_a_400()
    {
        var response = await _client.GetAsync($"{Cities}?orderByFields=geometry&f=json");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(400, (await ErrorAsync(response)).GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Order_by_a_bad_direction_is_a_400()
    {
        var response = await _client.GetAsync($"{Cities}?orderByFields=" + Uri.EscapeDataString("population SIDEWAYS") + "&f=json");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(400, (await ErrorAsync(response)).GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Order_by_is_validated_even_for_count_only()
    {
        var valid = await GetJsonAsync($"{Cities}?returnCountOnly=true&orderByFields=" + Uri.EscapeDataString("population DESC") + "&f=json");
        Assert.Equal(8, valid.GetProperty("count").GetInt32());

        var invalid = await _client.GetAsync($"{Cities}?returnCountOnly=true&orderByFields=bogus&f=json");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task Order_by_does_not_disturb_ids_only()
    {
        var result = await GetJsonAsync($"{Cities}?returnIdsOnly=true&orderByFields=population&f=json");

        Assert.Equal("OBJECTID", result.GetProperty("objectIdFieldName").GetString());
        Assert.Equal(8, result.GetProperty("objectIds").GetArrayLength());
    }

    private async Task<JsonElement> GetJsonAsync(string path)
    {
        var response = await _client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    private static string[] Names(JsonElement result) =>
        result.GetProperty("features").EnumerateArray()
            .Select(feature => feature.GetProperty("attributes").GetProperty("name").GetString()!)
            .ToArray();

    private static async Task<JsonElement> ErrorAsync(HttpResponseMessage response)
    {
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        return body.GetProperty("error");
    }
}
